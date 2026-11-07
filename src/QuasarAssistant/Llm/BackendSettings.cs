using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QuasarAssistant.Llm;

[JsonConverter(typeof(JsonStringEnumConverter<BackendKind>))]
public enum BackendKind
{
    /// <summary>OpenAI-compatible chat completions: OpenRouter, OpenAI, llama.cpp, vLLM.</summary>
    OpenAi,

    /// <summary>The Anthropic Messages API.</summary>
    Anthropic,
}

[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>One model at one provider, as Clangy's <c>[[backends]]</c>. The API key is stored apart, encrypted.</summary>
public sealed record BackendSettings
{
    public string Name { get; init; } = "";
    public BackendKind Kind { get; init; } = BackendKind.OpenAi;

    /// <summary>Base URL including <c>/v1</c>, e.g. https://openrouter.ai/api/v1 or https://api.anthropic.com/v1.</summary>
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>Lower is preferred; it orders the backend list on the settings page.</summary>
    public int Priority { get; init; } = 1;
    public int Parallel { get; init; } = 1;
    public int ContextWindow { get; init; } = 131072;
    public int MaxTokens { get; init; } = 8192;
    public ThinkingLevel Thinking { get; init; } = ThinkingLevel.None;

    /// <summary>Seconds without data before the first event; null waits forever.</summary>
    public double? FirstByteTimeout { get; init; } = 60;

    /// <summary>Seconds without data once the stream started; null waits forever.</summary>
    public double? StallTimeout { get; init; } = 60;

    /// <summary>Seconds a failed backend gets no requests; doubles with each further failure in a row.</summary>
    public double Cooldown { get; init; } = 60;
    public double MaxCooldown { get; init; } = 1800;

    /// <summary>USD per million tokens, for showing the monitor's budget and spending in money. Null: unknown.</summary>
    public double? InputPrice { get; init; }
    public double? CachedInputPrice { get; init; }
    public double? OutputPrice { get; init; }

    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>Merged into every request body, e.g. OpenRouter's <c>provider</c> or <c>models</c>.</summary>
    public JsonObject? ExtraBody { get; init; }
}
