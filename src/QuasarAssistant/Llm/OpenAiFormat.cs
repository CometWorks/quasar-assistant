using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuasarAssistant.Llm;

/// <summary>OpenAI-compatible chat completions, streamed.</summary>
internal static class OpenAiFormat
{
    public static JsonObject Body(BackendSettings backend, ChatRequest request)
    {
        var messages = new JsonArray();
        if (request.System.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var message in JsonHelpers.ForModel(request.Messages))
            // An empty answer, such as one that only thought, is nothing to send back.
            if (
                message
                is not { Role: ChatRole.Assistant, Text: "", ToolCalls: null or { Count: 0 } }
            )
                messages.Add(Message(message));
        var body = new JsonObject
        {
            ["model"] = backend.Model,
            ["max_tokens"] = backend.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = messages,
        };
        if (request.Tools.Count > 0)
            body["tools"] = Tools(request.Tools);
        if (backend.Thinking != ThinkingLevel.None)
            body["reasoning_effort"] = backend.Thinking.ToString().ToLowerInvariant();
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
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tool.Name,
                                ["description"] = tool.Description,
                                ["parameters"] = JsonNode.Parse(tool.Schema.GetRawText()),
                            },
                        }
                )
                .ToArray()
        );

    private static JsonObject Message(ChatMessage message) =>
        message.Role switch
        {
            ChatRole.User => new JsonObject { ["role"] = "user", ["content"] = message.ModelText },
            ChatRole.Tool => new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = message.ToolCallId,
                ["content"] = message.Text,
            },
            _ => Assistant(message),
        };

    private static JsonObject Assistant(ChatMessage message)
    {
        var result = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = message.Text.Length > 0 ? message.Text : null,
        };
        if (message.ToolCalls is { Count: > 0 } calls)
            result["tool_calls"] = new JsonArray(
                calls
                    .Select(call =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = call.Name,
                                    ["arguments"] = call.Arguments,
                                },
                            }
                    )
                    .ToArray()
            );
        return result;
    }

    internal sealed class Parser : StreamParser
    {
        private readonly SortedDictionary<int, Call> _calls = [];
        private string? _finish;
        private string? _model;
        private TokenUsage _usage = TokenUsage.None;

        public override string? Accept(JsonElement data)
        {
            TextChanged = false;
            if (data.Get("error").ValueKind != JsonValueKind.Undefined)
                return Truncate(data.Get("error").GetRawText());
            _model = data.GetString("model") ?? _model;
            if (data.Get("usage") is { ValueKind: JsonValueKind.Object } usage)
                _usage = new TokenUsage(
                    usage.GetInt("prompt_tokens"),
                    usage.Get("prompt_tokens_details").GetInt("cached_tokens"),
                    usage.GetInt("completion_tokens")
                );
            // llama.cpp reports its prompt cache hits here.
            if (data.Get("timings").Get("cache_n") is { ValueKind: JsonValueKind.Number } cached)
                _usage = _usage with { Cached = cached.GetInt32() };
            if (data.Get("choices") is not { ValueKind: JsonValueKind.Array } choices)
                return null;
            foreach (var choice in choices.EnumerateArray())
            {
                var delta = choice.Get("delta");
                AddText(delta.GetString("content"));
                if (delta.Get("tool_calls") is { ValueKind: JsonValueKind.Array } calls)
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = call.GetInt("index");
                        if (!_calls.TryGetValue(index, out var current))
                            _calls[index] = current = new Call();
                        var function = call.Get("function");
                        current.Id = call.GetString("id") ?? current.Id;
                        current.Name = function.GetString("name") ?? current.Name;
                        current.Arguments.Append(function.GetString("arguments"));
                    }
                if (choice.GetString("finish_reason") is { } finish)
                {
                    if (finish == "error")
                        return "finish_reason error";
                    _finish = finish;
                }
            }
            return null;
        }

        public override ChatResponse? Finish()
        {
            if (_finish is null)
                return null;
            var calls = _calls
                .Values.Select(
                    (call, i) =>
                        new ToolCall(
                            call.Id.Length > 0 ? call.Id : "call_" + i,
                            call.Name,
                            call.Arguments.ToString()
                        )
                )
                .ToList();
            var stop = _finish switch
            {
                "length" => StopKind.Length,
                "content_filter" => StopKind.Refusal,
                _ when calls.Count > 0 => StopKind.ToolUse,
                _ => StopKind.Done,
            };
            return new ChatResponse(Text.ToString(), calls, [], stop, _usage, _model);
        }

        private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;

        private sealed class Call
        {
            public string Id = "";
            public string Name = "";
            public readonly StringBuilder Arguments = new();
        }
    }
}
