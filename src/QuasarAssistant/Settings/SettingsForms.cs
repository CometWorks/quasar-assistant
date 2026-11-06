namespace QuasarAssistant.Settings;

/// <summary>A role's settings as the settings page edits them.</summary>
public sealed class RoleForm
{
    public List<string> Backends { get; set; } = [];
    public string RoleText { get; set; } = "";
    public Dictionary<string, ToolMode> AreaModes { get; set; } = [];
    public Dictionary<string, ToolMode> OperationModes { get; set; } = [];
    public List<McpServerForm> McpServers { get; set; } = [];
    public int MaxConcurrentRuns { get; set; }
    public int QueueWaitSeconds { get; set; }
    public int RunDeadlineSeconds { get; set; }
    public int MaxTurns { get; set; }

    public static RoleForm From(RoleSettings role) =>
        new()
        {
            Backends = [.. role.Backends],
            RoleText = role.RoleText,
            AreaModes = new(role.AreaModes),
            OperationModes = new(role.OperationModes),
            McpServers = role.McpServers.Select(McpServerForm.From).ToList(),
            MaxConcurrentRuns = role.MaxConcurrentRuns,
            QueueWaitSeconds = role.QueueWaitSeconds,
            RunDeadlineSeconds = role.RunDeadlineSeconds,
            MaxTurns = role.MaxTurns,
        };

    public RoleSettings ToSettings() =>
        new()
        {
            Backends = [.. Backends],
            RoleText = RoleText.Trim(),
            AreaModes = new(AreaModes),
            OperationModes = new(OperationModes),
            McpServers = McpServers.Select(server => server.ToSettings()).ToList(),
            MaxConcurrentRuns = Math.Max(1, MaxConcurrentRuns),
            QueueWaitSeconds = Math.Max(1, QueueWaitSeconds),
            RunDeadlineSeconds = Math.Max(10, RunDeadlineSeconds),
            MaxTurns = Math.Max(1, MaxTurns),
        };

    public string? Validate()
    {
        var names = McpServers.Select(server => server.Name.Trim()).ToList();
        if (
            names.Any(name =>
                name.Length == 0
                || name.Length > 32
                || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            )
        )
            return "An MCP server name is 1 to 32 letters, digits, - or _.";
        if (names.Distinct().Count() != names.Count)
            return "Two MCP servers have the same name.";
        if (
            McpServers.Any(server =>
                !Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https")
            )
        )
            return "An MCP server's URL must be an http or https URL.";
        if (
            McpServers.Any(server =>
                server
                    .Headers.Split('\n')
                    .Any(line => line.Trim().Length > 0 && !line.Contains(':'))
            )
        )
            return "Write each MCP header as Name: value.";
        return null;
    }
}

/// <summary>A remote MCP server on the settings page. Header values are write-only, like API keys.</summary>
public sealed class McpServerForm
{
    public string? Original { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public HashSet<string> ReadOnlyTools { get; set; } = [];

    /// <summary>New headers, one <c>Name: value</c> per line; empty keeps the stored ones.</summary>
    public string Headers { get; set; } = "";
    public bool RemoveHeaders { get; set; }

    public static McpServerForm From(McpServerSettings server) =>
        new()
        {
            Original = server.Name,
            Name = server.Name,
            Url = server.Url,
            Enabled = server.Enabled,
            ReadOnlyTools = [.. server.ReadOnlyTools],
        };

    public McpServerSettings ToSettings() =>
        new()
        {
            Name = Name.Trim(),
            Url = Url.Trim(),
            Enabled = Enabled,
            ReadOnlyTools = ReadOnlyTools.Order(StringComparer.Ordinal).ToList(),
        };

    public Dictionary<string, string> ParsedHeaders() =>
        Headers
            .Split('\n')
            .Where(line => line.Contains(':'))
            .Select(line => line.Split(':', 2))
            .GroupBy(parts => parts[0].Trim())
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim());
}

/// <summary>The player support section: on or off, limits, notice, book and restore rules.</summary>
public sealed class PlayerForm
{
    public bool Enabled { get; set; }
    public int MaxMessages { get; set; }
    public int RefillMinutes { get; set; }
    public int RefillMessages { get; set; }
    public int ActionsPerDay { get; set; }
    public string PrivacyNotice { get; set; } = "";
    public bool QuasarBook { get; set; }
    public string Placement { get; set; } = RestorePlacements.Original;
    public bool EmptyInventories { get; set; }
    public double ForeignExclusionMeters { get; set; }
    public double MaxSearchMeters { get; set; }
    public HashSet<string> AllowedCauses { get; set; } = [];
    public int? PerDay { get; set; }
    public int? PerWeek { get; set; }
    public int? PerMonth { get; set; }

    public static PlayerForm From(PlayerSupportSettings player) =>
        new()
        {
            Enabled = player.Enabled,
            MaxMessages = player.Limits.MaxMessages,
            RefillMinutes = player.Limits.RefillMinutes,
            RefillMessages = player.Limits.RefillMessages,
            ActionsPerDay = player.Limits.ActionsPerDay,
            PrivacyNotice = player.PrivacyNotice,
            QuasarBook = player.QuasarBook,
            Placement = player.Restore.Placement,
            EmptyInventories = player.Restore.EmptyInventories,
            ForeignExclusionMeters = player.Restore.ForeignExclusionMeters,
            MaxSearchMeters = player.Restore.MaxSearchMeters,
            AllowedCauses = [.. player.Restore.AllowedCauses],
            PerDay = player.Restore.PerDay,
            PerWeek = player.Restore.PerWeek,
            PerMonth = player.Restore.PerMonth,
        };

    public PlayerSupportSettings ToSettings() =>
        new()
        {
            Enabled = Enabled,
            Limits = new()
            {
                MaxMessages = Math.Max(0, MaxMessages),
                RefillMinutes = Math.Max(1, RefillMinutes),
                RefillMessages = Math.Max(0, RefillMessages),
                ActionsPerDay = Math.Max(0, ActionsPerDay),
            },
            PrivacyNotice = PrivacyNotice.Trim(),
            QuasarBook = QuasarBook,
            Restore = new()
            {
                Placement = RestorePlacements.All.Contains(Placement)
                    ? Placement
                    : RestorePlacements.Original,
                EmptyInventories = EmptyInventories,
                ForeignExclusionMeters = Math.Max(0, ForeignExclusionMeters),
                MaxSearchMeters = Math.Max(0, MaxSearchMeters),
                AllowedCauses = RestoreRules.Causes.Where(AllowedCauses.Contains).ToList(),
                PerDay = PerDay is { } day ? Math.Max(0, day) : null,
                PerWeek = PerWeek is { } week ? Math.Max(0, week) : null,
                PerMonth = PerMonth is { } month ? Math.Max(0, month) : null,
            },
        };
}
