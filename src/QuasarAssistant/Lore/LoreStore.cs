using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Lore;

public static class LoreKinds
{
    public const string None = "none";
    public const string Text = "text";
    public const string Git = "git";
}

/// <summary>A lore page; <paramref name="AdminOnly"/> pages (under an <c>admin/</c> folder of a skill) are never
/// given to the player agent.</summary>
public sealed record LorePage(string Name, string Text, bool AdminOnly);

/// <summary>One server's or cluster's lore as stored in <c>lore.json</c>.</summary>
/// <param name="Target">The server's or cluster's unique name as the admin gave it.</param>
public sealed record LoreEntry
{
    public string Target { get; init; } = "";
    public string Kind { get; init; } = LoreKinds.None;
    public string Text { get; init; } = "";
    public string GitUrl { get; init; } = "";

    /// <summary>The last good fetch of a Git skill: its commit and pages. Kept when a later fetch fails.</summary>
    public string? Revision { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public List<LorePage> Fetched { get; init; } = [];
    public DateTimeOffset? AttemptedAt { get; init; }

    /// <summary>Why the last fetch failed; null after a good one.</summary>
    public string? Error { get; init; }
}

/// <summary>What the API and the settings page show of an entry; the token only as set or not.</summary>
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
    IReadOnlyList<string> Pages
);

/// <param name="Token">A Git access token for a private repository: null keeps the stored one, empty removes it.</param>
public sealed record LoreSetRequest(
    string Target,
    string Kind,
    string? Text = null,
    string? GitUrl = null,
    string? Token = null
);

public sealed record LoreTargetRequest(string Target);

/// <summary>
/// The lore of each server and cluster, for the agents to read as <c>lore/…</c> skill pages. A cluster has one lore
/// for all its nodes: chat from a node names the cluster. Free text is one page; a Git skill is fetched when it is
/// set, when an admin refreshes it and every <see cref="RefreshInterval"/>, and its last good copy stays in use
/// while a fetch fails. Tokens are kept in <c>lore-tokens.json</c>, encrypted like the backends' API keys.
/// </summary>
public sealed class LoreStore
{
    public const string PagePrefix = "lore/";
    public const string TextPage = PagePrefix + "world";
    public const int MaxTextLength = 200_000;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly string _path;
    private readonly string _tokensPath;
    private readonly string _workRoot;
    private readonly IDataProtector _protector;
    private readonly GitFetcher _git;
    private readonly TimeProvider _time;
    private readonly ILogger<LoreStore> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private Dictionary<string, LoreEntry> _entries;
    private Dictionary<string, string> _tokens;

    public LoreStore(
        AssistantPaths paths,
        IDataProtectionProvider protection,
        GitFetcher git,
        TimeProvider time,
        ILogger<LoreStore> logger
    )
    {
        _path = Path.Combine(paths.DataDirectory, "lore.json");
        _tokensPath = Path.Combine(paths.DataDirectory, "lore-tokens.json");
        _workRoot = Path.Combine(paths.DataDirectory, "lore-fetch");
        _protector = protection.CreateProtector("quasar.assistant.lore-tokens");
        _git = git;
        _time = time;
        _logger = logger;
        _entries = JsonFiles.Read<Dictionary<string, LoreEntry>>(_path) ?? [];
        _tokens = JsonFiles.Read<Dictionary<string, string>>(_tokensPath) ?? [];
    }

    /// <summary>Lets tests use <c>file://</c> repositories; admins only get https.</summary>
    internal bool AllowFileUrls { get; set; }

    /// <summary>The key of a server's or cluster's unique name, with or without <c>cluster:</c>.</summary>
    public static string? Key(string? target)
    {
        var key = target?.Trim() ?? "";
        if (key.StartsWith("cluster:", StringComparison.OrdinalIgnoreCase))
            key = key["cluster:".Length..].Trim();
        return key.Length == 0 ? null : key.ToLowerInvariant();
    }

    public IReadOnlyList<LoreView> List()
    {
        lock (_lock)
            return _entries
                .Values.OrderBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
                .Select(View)
                .ToList();
    }

    public LoreView? Get(string target)
    {
        lock (_lock)
            return Key(target) is { } key && _entries.TryGetValue(key, out var entry)
                ? View(entry)
                : null;
    }

    /// <summary>The pages of a server's or cluster's lore the role may read; none when it has none.</summary>
    public IReadOnlyList<SkillPage> Pages(string? target, string role)
    {
        LoreEntry? entry;
        lock (_lock)
            if (Key(target) is not { } key || !_entries.TryGetValue(key, out entry))
                return [];
        return PagesOf(entry)
            .Where(page => role != AssistantRoles.Player || !page.AdminOnly)
            .Select(page => new SkillPage(page.Name, page.Text))
            .ToList();
    }

    /// <summary>Sets or clears a lore. A Git skill is fetched before this returns; a failed fetch is saved as the
    /// entry's error, not thrown. Throws <see cref="ArgumentException"/> for invalid input.</summary>
    public async Task<LoreView?> SetAsync(LoreSetRequest request, CancellationToken cancel)
    {
        var key =
            Key(request.Target)
            ?? throw new ArgumentException("Name the server or cluster by its unique name.");
        var target = request.Target.Trim();
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "";
        switch (kind)
        {
            case LoreKinds.None:
                lock (_lock)
                {
                    _entries.Remove(key);
                    SaveEntries();
                    if (_tokens.Remove(key))
                        SaveTokens();
                }
                return null;
            case LoreKinds.Text:
                var text = (request.Text ?? "").ReplaceLineEndings("\n").Trim();
                if (text.Length == 0)
                    throw new ArgumentException(
                        "The lore text is empty; set the kind to none to remove the lore."
                    );
                if (text.Length > MaxTextLength)
                    throw new ArgumentException(
                        $"The lore text is over {MaxTextLength} characters; use a Git skill for more."
                    );
                lock (_lock)
                {
                    _entries[key] = new LoreEntry
                    {
                        Target = target,
                        Kind = LoreKinds.Text,
                        Text = text,
                    };
                    SaveEntries();
                    if (_tokens.Remove(key))
                        SaveTokens();
                    return View(_entries[key]);
                }
            case LoreKinds.Git:
                if (!GitSkillUrl.TryParse(request.GitUrl, AllowFileUrls, out _, out var error))
                    throw new ArgumentException(error);
                lock (_lock)
                {
                    var previous = _entries.GetValueOrDefault(key);
                    var url = request.GitUrl!.Trim();
                    _entries[key] =
                        previous is { Kind: LoreKinds.Git } && previous.GitUrl == url
                            ? previous with
                            {
                                Target = target,
                            }
                            : new LoreEntry
                            {
                                Target = target,
                                Kind = LoreKinds.Git,
                                GitUrl = url,
                            };
                    if (request.Token is not null)
                    {
                        if (request.Token.Length == 0)
                            _tokens.Remove(key);
                        else
                            _tokens[key] = _protector.Protect(request.Token.Trim());
                        SaveTokens();
                    }
                    SaveEntries();
                }
                return await RefreshAsync(target, cancel);
            default:
                throw new ArgumentException("The kind is none, text or git.");
        }
    }

    /// <summary>Fetches a Git skill again. Null when the target has no Git skill.</summary>
    public async Task<LoreView?> RefreshAsync(string target, CancellationToken cancel)
    {
        var key = Key(target);
        LoreEntry? entry;
        string? token = null;
        lock (_lock)
        {
            if (key is null || !_entries.TryGetValue(key, out entry) || entry.Kind != LoreKinds.Git)
                return null;
            if (_tokens.TryGetValue(key, out var stored))
                token = _protector.Unprotect(stored);
        }
        GitSkill? skill = null;
        string? failure;
        await _fetching.WaitAsync(cancel);
        try
        {
            if (!GitSkillUrl.TryParse(entry.GitUrl, AllowFileUrls, out var url, out failure))
                throw new LoreFetchException(failure!);
            skill = await _git.FetchAsync(url!, token, _workRoot, cancel);
            failure = null;
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
                || current.Kind != LoreKinds.Git
                || current.GitUrl != entry.GitUrl
            )
                return Get(target);
            current = skill is null
                ? current with
                {
                    AttemptedAt = now,
                    Error = failure,
                }
                : current with
                {
                    AttemptedAt = now,
                    FetchedAt = now,
                    Revision = skill.Revision,
                    Fetched = skill.Files.Select(ToPage).ToList(),
                    Error = null,
                };
            _entries[key] = current;
            SaveEntries();
            if (failure is not null)
                _logger.LogWarning(
                    "The lore skill of {Target} could not be fetched: {Error}",
                    current.Target,
                    failure
                );
            return View(current);
        }
    }

    /// <summary>Fetches the Git skills last tried more than <see cref="RefreshInterval"/> ago.</summary>
    public async Task RefreshDueAsync(CancellationToken cancel)
    {
        var due = _time.GetUtcNow() - RefreshInterval;
        List<string> targets;
        lock (_lock)
            targets = _entries
                .Values.Where(entry => entry.Kind == LoreKinds.Git && !(entry.AttemptedAt > due))
                .Select(entry => entry.Target)
                .ToList();
        foreach (var target in targets)
            await RefreshAsync(target, cancel);
    }

    private static LorePage ToPage((string Path, string Text) file)
    {
        var name = file.Path[..^".md".Length];
        var adminOnly = name.Split('/')
            .SkipLast(1)
            .Any(folder => folder.Equals("admin", StringComparison.OrdinalIgnoreCase));
        return new(PagePrefix + name, file.Text, adminOnly);
    }

    private static IEnumerable<LorePage> PagesOf(LoreEntry entry) =>
        entry.Kind == LoreKinds.Text ? [new LorePage(TextPage, entry.Text, false)] : entry.Fetched;

    private LoreView View(LoreEntry entry) =>
        new(
            entry.Target,
            entry.Kind,
            entry.Text,
            entry.GitUrl,
            _tokens.ContainsKey(Key(entry.Target)!),
            entry.Revision,
            entry.FetchedAt,
            entry.AttemptedAt,
            entry.Error,
            PagesOf(entry)
                .Select(page => page.AdminOnly ? page.Name + " (admins only)" : page.Name)
                .ToList()
        );

    private void SaveEntries() => JsonFiles.Write(_path, _entries);

    private void SaveTokens() => JsonFiles.Write(_tokensPath, _tokens);
}
