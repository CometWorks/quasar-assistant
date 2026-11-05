using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuasarAssistant.Llm;

namespace QuasarAssistant.Tests;

/// <summary>Answers a request: gets its JSON body and writes the response.</summary>
public delegate Task StubReply(JsonElement request, HttpResponse response, CancellationToken token);

/// <summary>
/// An in-process model server on Kestrel speaking both streaming formats. Each backend has its own path
/// (<c>/name/v1</c>) and a queue of scripted replies; every request body is recorded.
/// </summary>
public sealed class StubLlmServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<StubReply>> _replies = new();

    public ConcurrentQueue<(string Backend, string Path, JsonElement Body)> Requests { get; } =
        new();

    private StubLlmServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    public static async Task<StubLlmServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new StubLlmServer(app);
        app.MapPost("/{backend}/v1/{*path}", server.HandleAsync);
        await app.StartAsync();
        server.Url = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return server;
    }

    public string BaseUrl(string backend) => $"{Url}/{backend}/v1";

    public void Enqueue(string backend, params StubReply[] replies)
    {
        var queue = _replies.GetOrAdd(backend, _ => new());
        foreach (var reply in replies)
            queue.Enqueue(reply);
    }

    public IReadOnlyList<JsonElement> BodiesOf(string backend) =>
        Requests.Where(r => r.Backend == backend).Select(r => r.Body).ToList();

    private async Task HandleAsync(string backend, string path, HttpContext http)
    {
        var body = (await JsonDocument.ParseAsync(http.Request.Body)).RootElement.Clone();
        Requests.Enqueue((backend, path, body));
        if (!_replies.TryGetValue(backend, out var queue) || !queue.TryDequeue(out var reply))
        {
            http.Response.StatusCode = 500;
            await http.Response.WriteAsync("no scripted reply");
            return;
        }
        try
        {
            await reply(body, http.Response, http.RequestAborted);
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>A port nothing listens on.</summary>
    public static string ClosedUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/v1";
    }
}

/// <summary>Scripted replies in either format.</summary>
public static class Replies
{
    public static StubReply Status(int status, string? retryAfter = null) =>
        async (_, response, token) =>
        {
            response.StatusCode = status;
            if (retryAfter is not null)
                response.Headers.RetryAfter = retryAfter;
            await response.WriteAsync("{\"error\":\"scripted\"}", token);
        };

    public static StubReply Answer(
        BackendKind kind,
        string text,
        IReadOnlyList<ToolCall>? calls = null,
        string? stop = null,
        int promptTokens = 100,
        int cachedTokens = 0
    ) =>
        Events(
            kind == BackendKind.Anthropic
                ? AnthropicEvents(text, calls ?? [], stop, promptTokens, cachedTokens)
                : OpenAiEvents(text, calls ?? [], stop, promptTokens, cachedTokens)
        );

    /// <summary>A reply that depends on the request, such as the tools it offers.</summary>
    public static StubReply Decide(Func<JsonElement, StubReply> decide) =>
        (request, response, token) => decide(request)(request, response, token);

    public static StubReply ErrorInStream(BackendKind kind) =>
        Events(
            kind == BackendKind.Anthropic
                ?
                [
                    ("message_start", Message()),
                    (
                        "error",
                        new JsonObject
                        {
                            ["type"] = "error",
                            ["error"] = new JsonObject { ["type"] = "overloaded_error" },
                        }
                    ),
                ]
                :
                [
                    (
                        null,
                        new JsonObject
                        {
                            ["error"] = new JsonObject { ["message"] = "provider failed" },
                        }
                    ),
                ]
        );

    /// <summary>Headers, then silence.</summary>
    public static StubReply Silent() =>
        async (_, response, token) =>
        {
            response.ContentType = "text/event-stream";
            await response.Body.FlushAsync(token);
            await Task.Delay(Timeout.Infinite, token);
        };

    /// <summary>One event, then silence.</summary>
    public static StubReply Stalls(BackendKind kind) =>
        async (request, response, token) =>
        {
            response.ContentType = "text/event-stream";
            var first =
                kind == BackendKind.Anthropic
                    ? ("message_start", Message())
                    : ((string?)null, Chunk(new JsonObject { ["content"] = "Hel" }));
            await WriteAsync(response, first.Item1, first.Item2, token);
            await Task.Delay(Timeout.Infinite, token);
        };

    private static StubReply Events(IEnumerable<(string? Event, JsonObject Data)> events) =>
        async (_, response, token) =>
        {
            response.ContentType = "text/event-stream";
            foreach (var (name, data) in events)
                await WriteAsync(response, name, data, token);
        };

    private static async Task WriteAsync(
        HttpResponse response,
        string? name,
        JsonObject data,
        CancellationToken token
    )
    {
        var text = (name is null ? "" : $"event: {name}\n") + $"data: {data.ToJsonString()}\n\n";
        await response.WriteAsync(text, token);
        await response.Body.FlushAsync(token);
    }

    private static JsonObject Chunk(JsonObject delta, string? finish = null) =>
        new()
        {
            ["model"] = "stub",
            ["choices"] = new JsonArray(
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finish,
                }
            ),
        };

    private static IEnumerable<(string?, JsonObject)> OpenAiEvents(
        string text,
        IReadOnlyList<ToolCall> calls,
        string? stop,
        int prompt,
        int cached
    )
    {
        // Text in two pieces, to exercise the streaming.
        var half = text.Length / 2;
        if (text.Length > 0)
        {
            yield return (null, Chunk(new JsonObject { ["content"] = text[..half] }));
            yield return (null, Chunk(new JsonObject { ["content"] = text[half..] }));
        }
        for (var i = 0; i < calls.Count; i++)
            yield return (
                null,
                Chunk(
                    new JsonObject
                    {
                        ["tool_calls"] = new JsonArray(
                            new JsonObject
                            {
                                ["index"] = i,
                                ["id"] = calls[i].Id,
                                ["function"] = new JsonObject
                                {
                                    ["name"] = calls[i].Name,
                                    ["arguments"] = calls[i].Arguments,
                                },
                            }
                        ),
                    }
                )
            );
        yield return (
            null,
            Chunk(new JsonObject(), stop ?? (calls.Count > 0 ? "tool_calls" : "stop"))
        );
        yield return (
            null,
            new JsonObject
            {
                ["choices"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = prompt,
                    ["completion_tokens"] = 20,
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached },
                },
            }
        );
    }

    private static JsonObject Message(int prompt = 100, int cached = 0) =>
        new()
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["model"] = "stub-claude",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = prompt - cached,
                    ["cache_read_input_tokens"] = cached,
                    ["cache_creation_input_tokens"] = 0,
                    ["output_tokens"] = 1,
                },
            },
        };

    private static IEnumerable<(string?, JsonObject)> AnthropicEvents(
        string text,
        IReadOnlyList<ToolCall> calls,
        string? stop,
        int prompt,
        int cached
    )
    {
        yield return ("message_start", Message(prompt, cached));
        yield return ("ping", new JsonObject { ["type"] = "ping" });
        var index = 0;
        yield return (
            "content_block_start",
            new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" },
            }
        );
        yield return (
            "content_block_delta",
            new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "sig" },
            }
        );
        yield return (
            "content_block_stop",
            new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }
        );
        if (text.Length > 0)
        {
            yield return (
                "content_block_start",
                new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = index,
                    ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
                }
            );
            foreach (var piece in new[] { text[..(text.Length / 2)], text[(text.Length / 2)..] })
                yield return (
                    "content_block_delta",
                    new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = index,
                        ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = piece },
                    }
                );
            yield return (
                "content_block_stop",
                new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }
            );
        }
        foreach (var call in calls)
        {
            yield return (
                "content_block_start",
                new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = index,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = new JsonObject(),
                    },
                }
            );
            yield return (
                "content_block_delta",
                new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = index,
                    ["delta"] = new JsonObject
                    {
                        ["type"] = "input_json_delta",
                        ["partial_json"] = call.Arguments,
                    },
                }
            );
            yield return (
                "content_block_stop",
                new JsonObject { ["type"] = "content_block_stop", ["index"] = index++ }
            );
        }
        yield return (
            "message_delta",
            new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject
                {
                    ["stop_reason"] = stop ?? (calls.Count > 0 ? "tool_use" : "end_turn"),
                },
                ["usage"] = new JsonObject { ["output_tokens"] = 20 },
            }
        );
        yield return ("message_stop", new JsonObject { ["type"] = "message_stop" });
    }
}
