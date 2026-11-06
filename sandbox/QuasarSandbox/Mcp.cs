using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuasarSandbox;

/// <summary>
/// The MCP endpoint: JSON-RPC 2.0 over streamable HTTP, answered as plain JSON, without sessions. Two tools:
/// <c>run_code</c> and <c>read_file</c>. The workspace is the conversation the client names in
/// <c>_meta</c>; without one, each run gets a fresh workspace that is deleted after it.
/// </summary>
public sealed class Mcp(Sandbox sandbox)
{
    public const string ProtocolVersion = "2025-06-18";
    private const int ReadChunk = 12 * 1024;

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken token)
    {
        JsonObject? message;
        try
        {
            message =
                await JsonNode.ParseAsync(request.Body, cancellationToken: token) as JsonObject;
        }
        catch (JsonException)
        {
            return Results.Json(Error(null, -32700, "Parse error"));
        }
        if (message?["method"]?.GetValue<string>() is not { } method)
            return Results.Json(Error(message?["id"], -32600, "Invalid request"));
        var id = message["id"];
        if (id is null)
            return Results.Accepted(); // A notification.
        var parameters = message["params"] as JsonObject ?? [];
        try
        {
            return Results.Json(
                Result(
                    id,
                    method switch
                    {
                        "initialize" => new JsonObject
                        {
                            ["protocolVersion"] = ProtocolVersion,
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "Quasar Sandbox",
                                ["version"] = "1",
                            },
                        },
                        "ping" => new JsonObject(),
                        "tools/list" => new JsonObject { ["tools"] = Tools() },
                        "tools/call" => await CallAsync(parameters, token),
                        _ => throw new MethodNotFound(),
                    }
                )
            );
        }
        catch (MethodNotFound)
        {
            return Results.Json(Error(id, -32601, "Method not found: " + method));
        }
    }

    private async Task<JsonObject> CallAsync(JsonObject parameters, CancellationToken token)
    {
        var arguments = parameters["arguments"] as JsonObject ?? [];
        var conversation = parameters["_meta"]?[Sandbox.ConversationMeta]?.GetValue<string>();
        try
        {
            return parameters["name"]?.GetValue<string>() switch
            {
                "run_code" => await RunAsync(conversation, arguments, token),
                "read_file" => await ReadAsync(conversation, arguments, token),
                var name => Failure($"There is no tool named {name}."),
            };
        }
        catch (Exception error)
            when (error is SandboxException or InvalidOperationException or FormatException)
        {
            return Failure(error.Message);
        }
    }

    private async Task<JsonObject> RunAsync(
        string? conversation,
        JsonObject arguments,
        CancellationToken token
    )
    {
        var files = (arguments["files"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(file => new InputFile(
                file["path"]?.GetValue<string>() ?? "",
                file["content"]?.GetValue<string>() ?? "",
                file["base64"]?.GetValue<bool>() == true
            ))
            .ToList();
        var outcome = await sandbox.RunAsync(
            conversation,
            arguments["language"]?.GetValue<string>() ?? "",
            arguments["code"]?.GetValue<string>() ?? "",
            files,
            sandbox.ClampTimeout(arguments["timeoutSeconds"]?.GetValue<int>()),
            token
        );
        var text = new StringBuilder();
        text.AppendLine(
            outcome.TimedOut ? $"Timed out after {outcome.Seconds:0.0} s; the run was killed."
            : outcome.ExitCode is { } exit ? $"Exit code {exit} after {outcome.Seconds:0.0} s."
            : outcome.Stderr
        );
        Section(text, "stdout", outcome.Stdout, outcome.StdoutCut);
        Section(text, "stderr", outcome.Stderr, outcome.StderrCut);
        text.AppendLine(
            outcome.Changed.Count == 0
                ? "No file created or changed."
                : "Files created or changed: "
                    + string.Join(", ", outcome.Changed.Select(f => $"{f.Path} ({f.Size} bytes)"))
        );
        if (conversation is null)
            text.AppendLine("This run had no conversation, so its workspace is already gone.");
        return new JsonObject
        {
            ["content"] = new JsonArray(Text(text.ToString())),
            ["structuredContent"] = JsonSerializer.SerializeToNode(
                outcome,
                JsonSerializerOptions.Web
            ),
            ["isError"] = false,
        };
    }

    private async Task<JsonObject> ReadAsync(
        string? conversation,
        JsonObject arguments,
        CancellationToken token
    )
    {
        if (conversation is null)
            return Failure("Without a conversation there is no workspace to read.");
        var path = arguments["path"]?.GetValue<string>() ?? "";
        var offset = arguments["offset"]?.GetValue<long>() ?? 0;
        var (data, size) = await sandbox.ReadAsync(conversation, path, offset, ReadChunk, token);
        var end = offset + data.Length;
        string body;
        try
        {
            body = new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            body = "base64: " + Convert.ToBase64String(data);
        }
        var more =
            end < size ? $"\n[bytes {offset} to {end} of {size}. Read on with offset {end}.]" : "";
        return new JsonObject
        {
            ["content"] = new JsonArray(Text(body + more)),
            ["isError"] = false,
        };
    }

    private static void Section(StringBuilder text, string name, string body, bool cut)
    {
        if (body.Length == 0)
            return;
        text.AppendLine($"--- {name}{(cut ? " (cut at the output limit)" : "")} ---");
        text.AppendLine(body.TrimEnd());
    }

    private static JsonArray Tools() =>
        [
            new JsonObject
            {
                ["name"] = "run_code",
                ["description"] =
                    "Runs Python 3 or C# in a sandbox with no network. The working folder keeps its files "
                    + "between runs of the same conversation, within its size limit; files given here are written "
                    + "into it first. C# is one file with top-level statements and the .NET base library; there "
                    + "are no NuGet packages. Python has its standard library and what the host installed. Returns "
                    + "the exit code, stdout, stderr and the files the run created or changed.",
                ["inputSchema"] = JsonNode.Parse(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "language": { "type": "string", "enum": ["python", "csharp"] },
                        "code": { "type": "string", "description": "The program." },
                        "files": {
                          "type": "array",
                          "description": "Files to write into the working folder before the run.",
                          "items": {
                            "type": "object",
                            "properties": {
                              "path": { "type": "string", "description": "Relative to the working folder." },
                              "content": { "type": "string" },
                              "base64": { "type": "boolean", "description": "The content is base64." }
                            },
                            "required": ["path", "content"]
                          }
                        },
                        "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "The run is killed after this long." }
                      },
                      "required": ["language", "code"]
                    }
                    """
                ),
            },
            new JsonObject
            {
                ["name"] = "read_file",
                ["description"] =
                    "Reads a file from the working folder, 12 KiB at a time from the offset. A binary file comes "
                    + "back as base64.",
                ["inputSchema"] = JsonNode.Parse(
                    """
                    {
                      "type": "object",
                      "properties": {
                        "path": { "type": "string" },
                        "offset": { "type": "integer", "minimum": 0 }
                      },
                      "required": ["path"]
                    }
                    """
                ),
            },
        ];

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Failure(string message) =>
        new() { ["content"] = new JsonArray(Text(message)), ["isError"] = true };

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    private sealed class MethodNotFound : Exception;
}
