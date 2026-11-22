namespace QuasarAssistant.Monitoring;

public sealed record IncidentRun(
    DateTimeOffset Time,
    string Outcome,
    string Answer,
    long Tokens,
    double? Usd,
    List<string> Tools
);

/// <summary>A problem the checks found, from when it appeared until it went away.</summary>
public sealed record Incident
{
    public required string Id { get; init; }

    /// <summary>The check's key, such as <c>down:alpha</c>; the same key is the same problem.</summary>
    public required string Key { get; init; }
    public string Text { get; init; } = "";
    public string? Server { get; init; }
    public DateTimeOffset Opened { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public DateTimeOffset? Resolved { get; init; }
    public required string ConversationId { get; init; }
    public List<IncidentRun> Runs { get; init; } = [];
}
