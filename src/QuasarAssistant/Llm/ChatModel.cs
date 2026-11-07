using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuasarAssistant.Llm;

[JsonConverter(typeof(JsonStringEnumConverter<ChatRole>))]
public enum ChatRole
{
    User,
    Assistant,
    Tool,

    /// <summary>A note from the plugin, such as a proposal's outcome. It goes to the model as a <c>[host]</c>
    /// line at the start of the next user turn.</summary>
    Host,

    /// <summary>Shown to the asker only, never sent to a model.</summary>
    Error,
}

public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>One message of a conversation, in a form both client kinds translate.</summary>
public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset Time { get; init; }

    /// <summary>User turns: the <c>[host]</c> context lines that go before the text.</summary>
    public IReadOnlyList<string>? HostLines { get; init; }

    /// <summary>Assistant turns.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Assistant turns on an Anthropic backend: its thinking blocks, sent back unchanged.</summary>
    public IReadOnlyList<JsonElement>? Thinking { get; init; }

    /// <summary>Tool results.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }

    /// <summary>A tool result that created a proposal, so the chat shows its card.</summary>
    public string? ProposalId { get; init; }

    /// <summary>The user text as the model reads it: the host lines, then the text.</summary>
    [JsonIgnore]
    public string ModelText =>
        HostLines is { Count: > 0 }
            ? string.Join('\n', HostLines.Select(line => "[host] " + line))
                + (Text.Length > 0 ? "\n\n" + Text : "")
            : Text;
}

/// <param name="Schema">A JSON schema of an object.</param>
public sealed record ToolDefinition(string Name, string Description, JsonElement Schema);

public sealed record ChatRequest(
    string System,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<ChatMessage> Messages
);

public sealed record TokenUsage(int Prompt, int Cached, int Completion)
{
    public static readonly TokenUsage None = new(0, 0, 0);

    public TokenUsage Add(TokenUsage other) =>
        new(Prompt + other.Prompt, Cached + other.Cached, Completion + other.Completion);
}

public enum StopKind
{
    Done,
    ToolUse,

    /// <summary>Cut off by <c>max_tokens</c>.</summary>
    Length,
    Refusal,
}

public sealed record ChatResponse(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    IReadOnlyList<JsonElement> Thinking,
    StopKind Stop,
    TokenUsage Usage,
    string? Model
);
