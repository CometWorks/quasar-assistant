using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using QuasarAssistant.Lore;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Skills;

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

/// <summary>A skill as resolved from its source, with its text files.</summary>
public sealed record StoredSkill(
    string Name,
    string Folder,
    string Description,
    List<SkillFile> Files
);

/// <summary>One skill source of a role as stored in <c>skill-sources.json</c>.</summary>
public sealed record SkillSourceEntry
{
    public string Role { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Ref { get; init; } = "";
    public List<string> Paths { get; init; } = [];

    /// <summary>The uploaded file's name, for a ZIP or a SKILL.md.</summary>
    public string FileName { get; init; } = "";

    /// <summary>The commit of a Git source, the SHA-256 of an upload.</summary>
    public string? Revision { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public DateTimeOffset? AttemptedAt { get; init; }

    /// <summary>Why the last fetch failed; null after a good one. The last good skills stay in use.</summary>
    public string? Error { get; init; }
    public List<StoredSkill> Skills { get; init; } = [];
}

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

/// <summary>
/// The skill sources of each role: Git repository folders, uploaded ZIPs and single SKILL.md files. Each resolves to
/// skills the agents read as pages <c>&lt;skill&gt;</c> (its SKILL.md) and <c>&lt;skill&gt;/&lt;file&gt;</c> (its
/// support files). A Git source is fetched when it is set, when an admin refreshes it and every
/// <see cref="LoreStore.RefreshInterval"/>; its last good copy stays in use while a fetch fails. Tokens are kept in
/// <c>skill-source-tokens.json</c>, encrypted like the backends' API keys.
/// </summary>
public sealed class SkillSourceStore
{
    private readonly AssistantPaths _paths;
    private readonly string _path;
    private readonly string _tokensPath;
    private readonly string _workRoot;
    private readonly IDataProtector _protector;
    private readonly GitFetcher _git;
    private readonly TimeProvider _time;
    private readonly ILogger<SkillSourceStore> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private Dictionary<string, SkillSourceEntry> _entries;
    private Dictionary<string, string> _tokens;

    public SkillSourceStore(
        AssistantPaths paths,
        IDataProtectionProvider protection,
        GitFetcher git,
        TimeProvider time,
        ILogger<SkillSourceStore> logger
    )
    {
        _paths = paths;
        _path = Path.Combine(paths.DataDirectory, "skill-sources.json");
        _tokensPath = Path.Combine(paths.DataDirectory, "skill-source-tokens.json");
        _workRoot = Path.Combine(paths.DataDirectory, "skill-fetch");
        _protector = protection.CreateProtector("quasar.assistant.skill-source-tokens");
        _git = git;
        _time = time;
        _logger = logger;
        _entries = JsonFiles.Read<Dictionary<string, SkillSourceEntry>>(_path) ?? [];
        _tokens = JsonFiles.Read<Dictionary<string, string>>(_tokensPath) ?? [];
    }

    /// <summary>Lets tests use <c>file://</c> repositories; admins only get https.</summary>
    internal bool AllowFileUrls { get; set; }

    public IReadOnlyList<SkillSourceView> List(string? role = null)
    {
        lock (_lock)
            return _entries
                .Values.Where(entry => role is null || entry.Role == role)
                .OrderBy(entry => entry.Role, StringComparer.Ordinal)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(View)
                .ToList();
    }

    public SkillSourceView? Get(string role, string name)
    {
        lock (_lock)
            return _entries.TryGetValue(Key(role, name), out var entry) ? View(entry) : null;
    }

    /// <summary>The skills of a role's own sources, in source then skill name order.</summary>
    public IReadOnlyList<StoredSkill> Skills(string role)
    {
        lock (_lock)
            return _entries
                .Values.Where(entry => entry.Role == role)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .SelectMany(entry => entry.Skills)
                .ToList();
    }

    /// <summary>Adds or replaces a source. An upload is read before this returns and refused when it holds no usable
    /// skill; a Git source is fetched, and a failed fetch is saved as its error, not thrown. Throws
    /// <see cref="ArgumentException"/> for invalid input.</summary>
    public async Task<SkillSourceView> SetAsync(
        SkillSourceSetRequest request,
        CancellationToken cancel
    )
    {
        var role = CheckRole(request.Role);
        var name = request.Name?.Trim() ?? "";
        if (!SkillLibrary.IsValidName(name))
            throw new ArgumentException("A source name is 1 to 64 letters, digits, - or _.");
        var key = Key(role, name);
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "";
        switch (kind)
        {
            case SkillSourceKinds.Git:
                var (repository, gitRef, paths) = GitTarget(request);
                lock (_lock)
                {
                    var previous = _entries.GetValueOrDefault(key);
                    _entries[key] =
                        previous is { Kind: SkillSourceKinds.Git }
                        && previous.Repository == repository
                        && previous.Ref == gitRef
                        && previous.Paths.SequenceEqual(paths)
                            ? previous
                            : new SkillSourceEntry
                            {
                                Role = role,
                                Name = name,
                                Kind = SkillSourceKinds.Git,
                                Repository = repository,
                                Ref = gitRef,
                                Paths = paths,
                            };
                    if (request.Token is not null)
                    {
                        if (request.Token.Trim().Length == 0)
                            _tokens.Remove(key);
                        else
                            _tokens[key] = _protector.Protect(request.Token.Trim());
                        SaveTokens();
                    }
                    SaveEntries();
                }
                return (await RefreshAsync(role, name, cancel))!;
            case SkillSourceKinds.Zip:
            case SkillSourceKinds.File:
                var bytes = Decode(request.Content);
                IReadOnlyList<FoundSkill> found;
                try
                {
                    found =
                        kind == SkillSourceKinds.Zip
                            ? SkillTree.Find(SkillTree.ReadZip(bytes), name)
                            : SkillTree.Find([(SkillTree.SkillFileName, bytes)], name);
                }
                catch (LoreFetchException error)
                {
                    throw new ArgumentException(error.Message);
                }
                var now = _time.GetUtcNow();
                lock (_lock)
                {
                    if (Conflict(role, name, found) is { } conflict)
                        throw new ArgumentException(conflict);
                    _entries[key] = new SkillSourceEntry
                    {
                        Role = role,
                        Name = name,
                        Kind = kind,
                        FileName = Path.GetFileName(request.FileName?.Trim() ?? ""),
                        Revision = SkillTree.Sha256(bytes),
                        FetchedAt = now,
                        AttemptedAt = now,
                        Skills = found.Select(Store).ToList(),
                    };
                    SaveEntries();
                    if (_tokens.Remove(key))
                        SaveTokens();
                    return View(_entries[key]);
                }
            default:
                throw new ArgumentException("The kind is git, zip or file.");
        }
    }

    /// <summary>Removes a source; false when there was none.</summary>
    public bool Remove(string role, string name)
    {
        var key = Key(CheckRole(role), name.Trim());
        lock (_lock)
        {
            if (!_entries.Remove(key))
                return false;
            SaveEntries();
            if (_tokens.Remove(key))
                SaveTokens();
            return true;
        }
    }

    /// <summary>Fetches a Git source again; an upload is returned as it is. Null when there is no such source.</summary>
    public async Task<SkillSourceView?> RefreshAsync(
        string role,
        string name,
        CancellationToken cancel
    )
    {
        var key = Key(CheckRole(role), name.Trim());
        SkillSourceEntry? entry;
        string? token = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out entry))
                return null;
            if (entry.Kind != SkillSourceKinds.Git)
                return View(entry);
            if (_tokens.TryGetValue(key, out var stored))
                token = _protector.Unprotect(stored);
        }
        (string Revision, List<FoundSkill> Skills)? fetched = null;
        string? failure = null;
        await _fetching.WaitAsync(cancel);
        try
        {
            fetched = await _git.CheckoutAsync(
                entry.Repository,
                entry.Ref,
                entry.Paths.Select(SparsePath).Where(path => path.Length > 0).Distinct().ToList(),
                token,
                _workRoot,
                (work, revision) => (revision, ReadCheckout(work, entry)),
                cancel
            );
        }
        catch (LoreFetchException error)
        {
            failure = error.Message;
        }
        finally
        {
            _fetching.Release();
        }
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            // The admin may have changed or removed it meanwhile.
            if (
                !_entries.TryGetValue(key, out var current)
                || current.Kind != entry.Kind
                || current.Repository != entry.Repository
                || current.Ref != entry.Ref
                || !current.Paths.SequenceEqual(entry.Paths)
            )
                return Get(role, name);
            if (
                fetched is { } got
                && Conflict(current.Role, current.Name, got.Skills) is { } conflict
            )
            {
                failure = conflict;
                fetched = null;
            }
            current = fetched is { } result
                ? current with
                {
                    AttemptedAt = now,
                    FetchedAt = now,
                    Revision = result.Revision,
                    Skills = result.Skills.Select(Store).ToList(),
                    Error = null,
                }
                : current with
                {
                    AttemptedAt = now,
                    Error = failure,
                };
            _entries[key] = current;
            SaveEntries();
            if (failure is not null)
                _logger.LogWarning(
                    "The skill source {Role}/{Name} could not be fetched: {Error}",
                    current.Role,
                    current.Name,
                    failure
                );
            return View(current);
        }
    }

    /// <summary>Fetches the Git sources last tried more than <see cref="LoreStore.RefreshInterval"/> ago.</summary>
    public async Task RefreshDueAsync(CancellationToken cancel)
    {
        var due = _time.GetUtcNow() - LoreStore.RefreshInterval;
        List<(string Role, string Name)> sources;
        lock (_lock)
            sources = _entries
                .Values.Where(entry =>
                    entry.Kind == SkillSourceKinds.Git && !(entry.AttemptedAt > due)
                )
                .Select(entry => (entry.Role, entry.Name))
                .ToList();
        foreach (var (role, name) in sources)
            await RefreshAsync(role, name, cancel);
    }

    /// <summary>The stored files, tokens still encrypted, for the settings page's revert.</summary>
    public SkillSourceSnapshot Snapshot()
    {
        lock (_lock)
            return new(new(_entries), new(_tokens));
    }

    public void Restore(SkillSourceSnapshot snapshot)
    {
        lock (_lock)
        {
            _entries = new(snapshot.Entries);
            _tokens = new(snapshot.Tokens);
            SaveEntries();
            SaveTokens();
        }
    }

    public static string CheckRole(string? role) =>
        AssistantRoles.All.FirstOrDefault(name => name == role?.Trim().ToLowerInvariant())
        ?? throw new ArgumentException($"The role is {string.Join(", ", AssistantRoles.All)}.");

    private (string Repository, string Ref, List<string> Paths) GitTarget(
        SkillSourceSetRequest request
    )
    {
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            if (!GitSkillUrl.TryParse(request.Url, AllowFileUrls, out var url, out var problem))
                throw new ArgumentException(problem);
            return (url!.Repository, url.Ref, url.Path.Length > 0 ? [url.Path] : []);
        }
        var repository = request.Repository?.Trim() ?? "";
        if (
            !GitSkillUrl.TryParse(repository, AllowFileUrls, out var parsed, out var error)
            || parsed!.Repository != repository
        )
            throw new ArgumentException(
                error ?? "Give the repository as its https clone URL, without # or a tree path."
            );
        var paths = (request.Paths ?? [])
            .Select(path => path.Trim().Replace('\\', '/').Trim('/'))
            .Distinct()
            .ToList();
        if (paths.Any(path => path.Split('/').Any(part => part is "." or "..")))
            throw new ArgumentException(
                "A path is a folder or SKILL.md in the repository, without . or .."
            );
        if (paths.Count > 20)
            throw new ArgumentException("A source has at most 20 paths.");
        return (
            repository,
            request.Ref?.Trim() ?? "",
            paths.Where(path => path.Length > 0).ToList()
        );
    }

    /// <summary>A path to a SKILL.md checks out its folder.</summary>
    private static string SparsePath(string path) =>
        path.EndsWith("/" + SkillTree.SkillFileName, StringComparison.OrdinalIgnoreCase)
            ? path[..path.LastIndexOf('/')]
        : path.Equals(SkillTree.SkillFileName, StringComparison.OrdinalIgnoreCase) ? ""
        : path;

    private static List<FoundSkill> ReadCheckout(string work, SkillSourceEntry entry)
    {
        var top = Path.GetFullPath(work);
        var skills = new List<FoundSkill>();
        foreach (var path in entry.Paths.Count == 0 ? [""] : entry.Paths)
        {
            var folder = SparsePath(path);
            var root = Path.GetFullPath(Path.Combine(work, folder));
            if (!root.StartsWith(top, StringComparison.Ordinal) || !Directory.Exists(root))
                throw new LoreFetchException($"There is no folder {folder} in the repository.");
            var files = SkillTree.ReadFolder(root);
            var fallback =
                folder.Length == 0 ? entry.Name : folder[(folder.LastIndexOf('/') + 1)..];
            try
            {
                skills.AddRange(
                    SkillTree
                        .Find(files, fallback)
                        // A path to a SKILL.md takes that skill only, not the ones in folders under it.
                        .Where(skill => folder == path || skill.Folder.Length == 0)
                        .Select(skill =>
                            skill with
                            {
                                Folder = string.Join(
                                    '/',
                                    new[] { folder, skill.Folder }.Where(part => part.Length > 0)
                                ),
                            }
                        )
                );
            }
            catch (LoreFetchException error) when (folder.Length > 0)
            {
                throw new LoreFetchException($"{folder}: {error.Message}");
            }
        }
        var twice = skills.GroupBy(skill => skill.Name).FirstOrDefault(group => group.Count() > 1);
        if (twice is not null)
            throw new LoreFetchException(
                $"Two skills are named {twice.Key}: in {string.Join(" and ", twice.Select(skill => skill.Folder))}."
            );
        return skills;
    }

    /// <summary>Why <paramref name="found"/> can't join the role: a name another source or a skill written on the
    /// settings page already has. The admin assistant and the monitor count as one, as the monitor reads the
    /// admin assistant's skills.</summary>
    private string? Conflict(string role, string source, IEnumerable<FoundSkill> found)
    {
        var names = found.Select(skill => skill.Name).ToList();
        var inline = SkillLibrary.InlineNames(_paths, role);
        if (names.FirstOrDefault(inline.Contains) is { } written)
            return $"A skill named {written} is already written on the settings page.";
        var shared = Shared(role);
        foreach (
            var other in _entries.Values.Where(entry =>
                shared.Contains(entry.Role) && !(entry.Role == role && entry.Name == source)
            )
        )
            if (other.Skills.FirstOrDefault(skill => names.Contains(skill.Name)) is { } taken)
                return other.Role == role
                    ? $"The source {other.Name} already has a skill named {taken.Name}."
                    : $"The source {other.Name} of the {other.Role} role already has a skill named {taken.Name}, and the monitor reads the admin assistant's skills.";
        return null;
    }

    /// <summary>The roles whose skill names must differ from the role's.</summary>
    internal static IReadOnlyList<string> Shared(string role) =>
        role is AssistantRoles.Admin or AssistantRoles.Monitor
            ? [AssistantRoles.Admin, AssistantRoles.Monitor]
            : [role];

    /// <summary>Whether a source of a role sharing names with <paramref name="role"/> has a skill named
    /// <paramref name="name"/>.</summary>
    public bool HasSkill(string role, string name)
    {
        var shared = Shared(role);
        lock (_lock)
            return _entries.Values.Any(entry =>
                shared.Contains(entry.Role) && entry.Skills.Any(skill => skill.Name == name)
            );
    }

    private static byte[] Decode(string? content)
    {
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Upload a file.");
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw new ArgumentException("The content is not valid base64.");
        }
    }

    private static StoredSkill Store(FoundSkill skill) =>
        new(skill.Name, skill.Folder, skill.Description, [.. skill.Files]);

    private static string Key(string role, string name) => role + "/" + name;

    private SkillSourceView View(SkillSourceEntry entry) =>
        new(
            entry.Role,
            entry.Name,
            entry.Kind,
            entry.Repository,
            entry.Ref,
            entry.Paths,
            entry.FileName,
            _tokens.ContainsKey(Key(entry.Role, entry.Name)),
            entry.Revision,
            entry.FetchedAt,
            entry.AttemptedAt,
            entry.Error,
            entry
                .Skills.Select(skill => new SkillView(
                    skill.Name,
                    skill.Folder,
                    skill.Description,
                    skill.Files.Select(file => file.Path).ToList()
                ))
                .ToList()
        );

    private void SaveEntries() => JsonFiles.Write(_path, _entries);

    private void SaveTokens() => JsonFiles.Write(_tokensPath, _tokens);
}

public sealed record SkillSourceSnapshot(
    Dictionary<string, SkillSourceEntry> Entries,
    Dictionary<string, string> Tokens
);
