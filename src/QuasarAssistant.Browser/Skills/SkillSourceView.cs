namespace QuasarAssistant.Skills;

public sealed record SkillView(
    string Name,
    string Folder,
    string Description,
    IReadOnlyList<string> Files
);

/// <summary>What the API and the settings page show of a source; the token only as set or not.</summary>
public sealed record SkillSourceView(
    string Role,
    string Name,
    string Kind,
    string Repository,
    string Ref,
    IReadOnlyList<string> Paths,
    string FileName,
    bool TokenSet,
    string? Revision,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? AttemptedAt,
    string? Error,
    IReadOnlyList<SkillView> Skills
);

/// <summary>Adds or replaces a role's skill source by its name.</summary>
/// <param name="Url">Instead of <paramref name="Repository"/>, <paramref name="Ref"/> and <paramref name="Paths"/>: a
/// GitHub, GitLab, Forgejo or Gitea tree URL of one skill folder, as the lore takes it.</param>
/// <param name="Token">A Git access token: null keeps the stored one, empty removes it.</param>
/// <param name="Content">The uploaded file: a ZIP or a SKILL.md, base64.</param>
public sealed record SkillSourceSetRequest(
    string Role,
    string Name,
    string Kind,
    string? Repository = null,
    string? Ref = null,
    IReadOnlyList<string>? Paths = null,
    string? Url = null,
    string? Token = null,
    string? Content = null,
    string? FileName = null
);

public static class SkillSourceKinds
{
    /// <summary>Folders of a Git repository at a ref, fetched by Quasar.</summary>
    public const string Git = "git";

    /// <summary>An uploaded ZIP; every SKILL.md in it is a skill.</summary>
    public const string Zip = "zip";

    /// <summary>One uploaded SKILL.md, without support files.</summary>
    public const string File = "file";

    public static readonly IReadOnlyList<string> All = [Git, Zip, File];
}
