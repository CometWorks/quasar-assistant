namespace QuasarAssistant.Storage;

/// <summary>A conversation handed to the admins by <c>ask_admin</c> or after a failed run.</summary>
public sealed record Handover
{
    public required string Id { get; init; }
    public DateTimeOffset Created { get; init; }
    public required string ConversationId { get; init; }

    /// <summary>The player's Steam ID or Discord ID, as the conversation is keyed.</summary>
    public string PlayerKey { get; init; } = "";
    public string PlayerName { get; init; } = "";

    /// <summary>Where the player asked, such as "in game on alpha".</summary>
    public string Source { get; init; } = "";
    public string Reason { get; init; } = "";

    /// <summary>The last messages of the conversation, plain text.</summary>
    public string Excerpt { get; init; } = "";
    public bool Handled { get; init; }
    public string? HandledBy { get; init; }
    public DateTimeOffset? HandledAt { get; init; }
}
