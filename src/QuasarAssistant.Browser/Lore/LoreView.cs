namespace QuasarAssistant.Lore;

public sealed record LoreView(
    string Target,
    string Kind,
    string Text,
    string GitUrl,
    bool TokenSet,
    string? Revision,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? AttemptedAt,
    string? Error,
    IReadOnlyList<string> Pages,
    string Folder = ""
);

/// <param name="Token">A Git access token for a private repository: null keeps the stored one, empty removes it.</param>
public sealed record LoreSetRequest(
    string Target,
    string Kind,
    string? Text = null,
    string? GitUrl = null,
    string? Token = null,
    string? Folder = null
);

public sealed record LoreTargetRequest(string Target)
{
    /// <summary>The key of a server's or cluster's unique name, with or without <c>cluster:</c>.</summary>
    public static string? Key(string? target)
    {
        var key = target?.Trim() ?? "";
        if (key.StartsWith("cluster:", StringComparison.OrdinalIgnoreCase))
            key = key["cluster:".Length..].Trim();
        return key.Length == 0 ? null : key.ToLowerInvariant();
    }
}

public static class LoreKinds
{
    public const string None = "none";
    public const string Text = "text";
    public const string Git = "git";
    public const string Folder = "folder";
}
