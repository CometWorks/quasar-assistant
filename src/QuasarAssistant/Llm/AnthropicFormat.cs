using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuasarAssistant.Llm;

/// <summary>The Anthropic Messages API, streamed, with explicit prompt cache breakpoints.</summary>
internal static class AnthropicFormat
{
    public static JsonObject Body(BackendSettings backend, ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = backend.Model,
            ["max_tokens"] = backend.MaxTokens,
            ["stream"] = true,
        };
        // Tools render before the system prompt, so this breakpoint caches both. An empty text block is refused.
        if (request.System.Length > 0)
            body["system"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.System,
                    ["cache_control"] = Ephemeral(),
                }
            );
        if (request.Tools.Count > 0)
            body["tools"] = Tools(request.Tools);
        body["messages"] = Messages(request.Messages);
        if (backend.Thinking != ThinkingLevel.None)
        {
            body["thinking"] = new JsonObject { ["type"] = "adaptive" };
            body["output_config"] = new JsonObject
            {
                ["effort"] = backend.Thinking.ToString().ToLowerInvariant(),
            };
        }
        JsonHelpers.Merge(body, backend.ExtraBody);
        return body;
    }

    public static JsonArray Tools(IReadOnlyList<ToolDefinition> tools) =>
        new(
            tools
                .Select(tool =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["input_schema"] = JsonNode.Parse(tool.Schema.GetRawText()),
                        }
                )
                .ToArray()
        );

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private static JsonArray Messages(IReadOnlyList<ChatMessage> messages)
    {
        // The API wants user and assistant turns in turn: tool results and the next user text share a turn.
        List<(string Role, JsonArray Content)> turns = [];
        foreach (var message in JsonHelpers.ForModel(messages))
        {
            var (role, blocks) = Blocks(message);
            if (blocks.Count == 0)
                continue;
            if (turns.Count > 0 && turns[^1].Role == role)
                foreach (var block in blocks)
                    turns[^1].Content.Add(block);
            else
                turns.Add((role, new JsonArray([.. blocks])));
        }
        // The last breakpoint caches the conversation so far for the next request of the run.
        if (turns.Count > 0 && turns[^1].Content[^1] is JsonObject last)
            last["cache_control"] = Ephemeral();
        return new JsonArray(
            turns
                .Select(turn =>
                    (JsonNode)new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content }
                )
                .ToArray()
        );
    }

    private static (string Role, List<JsonNode> Blocks) Blocks(ChatMessage message)
    {
        switch (message.Role)
        {
            case ChatRole.User:
                return (
                    "user",
                    [new JsonObject { ["type"] = "text", ["text"] = message.ModelText }]
                );
            case ChatRole.Tool:
                return (
                    "user",
                    [
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = message.ToolCallId,
                            ["content"] = message.Text,
                            ["is_error"] = message.IsError,
                        },
                    ]
                );
            default:
                List<JsonNode> blocks =
                [
                    .. (message.Thinking ?? []).Select(block =>
                        JsonNode.Parse(block.GetRawText())!
                    ),
                ];
                if (message.Text.Length > 0)
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
                foreach (var call in message.ToolCalls ?? [])
                    blocks.Add(
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = JsonHelpers.ArgumentsObject(call.Arguments),
                        }
                    );
                // Thinking alone is no answer to send back.
                return ("assistant", blocks.Count == (message.Thinking?.Count ?? 0) ? [] : blocks);
        }
    }

    internal sealed class Parser : StreamParser
    {
        private readonly Dictionary<
            int,
            (string Type, JsonObject Block, StringBuilder Buffer)
        > _blocks = [];
        private readonly List<ToolCall> _calls = [];
        private readonly List<JsonElement> _thinking = [];
        private string? _stop;
        private string? _model;
        private TokenUsage _usage = TokenUsage.None;

        public override string? Accept(JsonElement data)
        {
            TextChanged = false;
            switch (data.GetString("type"))
            {
                case "error":
                    var error = data.Get("error").GetRawText();
                    return error.Length > 500 ? error[..500] : error;
                case "message_start":
                    var message = data.Get("message");
                    _model = message.GetString("model");
                    Usage(message.Get("usage"));
                    break;
                case "content_block_start":
                    var block = data.Get("content_block");
                    var type = block.GetString("type") ?? "";
                    _blocks[data.GetInt("index")] = (
                        type,
                        JsonNode.Parse(block.GetRawText())!.AsObject(),
                        new StringBuilder()
                    );
                    if (type == "text")
                        AddText(block.GetString("text"));
                    break;
                case "content_block_delta":
                    if (!_blocks.TryGetValue(data.GetInt("index"), out var current))
                        break;
                    var delta = data.Get("delta");
                    switch (delta.GetString("type"))
                    {
                        case "text_delta":
                            AddText(delta.GetString("text"));
                            break;
                        case "input_json_delta":
                            current.Buffer.Append(delta.GetString("partial_json"));
                            break;
                        case "thinking_delta":
                            current.Buffer.Append(delta.GetString("thinking"));
                            break;
                        case "signature_delta":
                            current.Block["signature"] = delta.GetString("signature");
                            break;
                    }
                    break;
                case "content_block_stop":
                    if (_blocks.Remove(data.GetInt("index"), out var done))
                        Close(done.Type, done.Block, done.Buffer);
                    break;
                case "message_delta":
                    _stop = data.Get("delta").GetString("stop_reason") ?? _stop;
                    Usage(data.Get("usage"));
                    break;
            }
            return null;
        }

        private void Close(string type, JsonObject block, StringBuilder buffer)
        {
            switch (type)
            {
                case "tool_use":
                    _calls.Add(
                        new ToolCall(
                            block["id"]?.GetValue<string>() ?? "",
                            block["name"]?.GetValue<string>() ?? "",
                            buffer.Length > 0 ? buffer.ToString() : "{}"
                        )
                    );
                    break;
                case "thinking":
                    block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + buffer;
                    _thinking.Add(JsonSerializer.SerializeToElement(block));
                    break;
                case "redacted_thinking":
                    _thinking.Add(JsonSerializer.SerializeToElement(block));
                    break;
            }
        }

        // message_start carries the input tokens, message_delta the output tokens. Cached input isn't in input_tokens.
        private void Usage(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
                return;
            if (usage.TryGetProperty("input_tokens", out _))
            {
                var cached = usage.GetInt("cache_read_input_tokens");
                _usage = _usage with
                {
                    Prompt =
                        usage.GetInt("input_tokens")
                        + cached
                        + usage.GetInt("cache_creation_input_tokens"),
                    Cached = cached,
                };
            }
            if (usage.TryGetProperty("output_tokens", out _))
                _usage = _usage with { Completion = usage.GetInt("output_tokens") };
        }

        public override ChatResponse? Finish()
        {
            if (_stop is null)
                return null;
            var stop = _stop switch
            {
                "max_tokens" => StopKind.Length,
                "refusal" => StopKind.Refusal,
                "tool_use" => StopKind.ToolUse,
                _ => StopKind.Done,
            };
            return new ChatResponse(Text.ToString(), _calls, _thinking, stop, _usage, _model);
        }
    }
}
