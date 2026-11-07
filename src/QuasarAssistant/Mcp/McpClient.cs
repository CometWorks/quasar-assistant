using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuasarAssistant.Mcp;

/// <summary>A tool a remote MCP server offers, with its own description and input schema.</summary>
public sealed record McpTool(string Server, string Name, string Description, JsonElement Schema);

public sealed class McpException(string message) : Exception(message);

/// <summary>
/// A minimal MCP client over streamable HTTP: JSON-RPC 2.0 posted to the server's URL, answered as JSON or as a
/// short event stream. Each use opens its own session (<c>initialize</c>, then the request), so nothing is kept
/// between calls. Only <c>tools/list</c> and <c>tools/call</c>; no stdio servers.
/// </summary>
public sealed class McpClient(HttpClient http)
{
    public const string ProtocolVersion = "2025-06-18";
    public const string ConversationMeta = "quasar.assistant/conversation";
    private const int MaxTools = 500;

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(
        string server,
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken token
    )
    {
        var session = await InitializeAsync(url, headers, token);
        List<McpTool> tools = [];
        string? cursor = null;
        do
        {
            var result = await RequestAsync(
                url,
                headers,
                session,
                "tools/list",
                cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor },
                token
            );
            foreach (var tool in (result?["tools"] as JsonArray ?? []).OfType<JsonObject>())
                if (tool["name"]?.GetValue<string>() is { Length: > 0 } name)
                    tools.Add(
                        new McpTool(
                            server,
                            name,
                            tool["description"]?.GetValue<string>() ?? "",
                            JsonSerializer.SerializeToElement(
                                tool["inputSchema"] ?? new JsonObject { ["type"] = "object" }
                            )
                        )
                    );
            cursor = result?["nextCursor"]?.GetValue<string>();
        } while (cursor is not null && tools.Count < MaxTools);
        return tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
    }

    /// <returns>The <c>tools/call</c> result: <c>content</c>, maybe <c>structuredContent</c> and <c>isError</c>.</returns>
    public async Task<JsonNode?> CallAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string tool,
        JsonNode? arguments,
        CancellationToken token,
        string? conversation = null
    )
    {
        var session = await InitializeAsync(url, headers, token);
        var parameters = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments ?? new JsonObject(),
        };
        // Lets a server keep state per conversation, such as the sandbox's workspace.
        if (conversation is not null)
            parameters["_meta"] = new JsonObject { [ConversationMeta] = conversation };
        return await RequestAsync(url, headers, session, "tools/call", parameters, token);
    }

    private async Task<string?> InitializeAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken token
    )
    {
        var (_, session) = await SendAsync(
            url,
            headers,
            null,
            Message(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "Quasar Assistant",
                        ["version"] = "1",
                    },
                }
            ),
            token
        );
        await SendAsync(
            url,
            headers,
            session,
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            token
        );
        return session;
    }

    private async Task<JsonNode?> RequestAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? session,
        string method,
        JsonObject parameters,
        CancellationToken token
    ) => (await SendAsync(url, headers, session, Message(method, parameters), token)).Result;

    private async Task<(JsonNode? Result, string? Session)> SendAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? session,
        JsonObject message,
        CancellationToken token
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        if (session is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (message["method"]?.GetValue<string>() != "initialize")
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);

        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"The MCP server answered HTTP {(int)response.StatusCode}.");
        session ??= response.Headers.TryGetValues("Mcp-Session-Id", out var ids)
            ? ids.FirstOrDefault()
            : null;
        if (message["id"] is null)
            return (null, session); // A notification: 202 and no body.

        var body = await response.Content.ReadAsStringAsync(token);
        var answer =
            response.Content.Headers.ContentType?.MediaType == "text/event-stream"
                ? FromEvents(body, message["id"]!.GetValue<int>())
                : JsonNode.Parse(body);
        if (answer?["error"] is JsonObject error)
            throw new McpException(
                "The MCP server refused: " + (error["message"]?.GetValue<string>() ?? "no reason")
            );
        return (answer?["result"], session);
    }

    /// <summary>The JSON-RPC answer with the request's id among the stream's events.</summary>
    private static JsonNode? FromEvents(string body, int id)
    {
        foreach (var block in body.ReplaceLineEndings("\n").Split("\n\n"))
        {
            var data = string.Join(
                '\n',
                block
                    .Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].TrimStart())
            );
            if (data.Length == 0)
                continue;
            var node = JsonNode.Parse(data);
            if (node?["id"] is JsonValue value && value.TryGetValue<int>(out var got) && got == id)
                return node;
        }
        throw new McpException("The MCP server's event stream held no answer.");
    }

    private static JsonObject Message(string method, JsonObject parameters) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = parameters,
        };
}
