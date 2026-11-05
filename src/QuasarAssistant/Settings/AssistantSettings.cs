using System.Text.Json.Serialization;
using QuasarAssistant.Llm;

namespace QuasarAssistant.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<ToolMode>))]
public enum ToolMode
{
    Off,
    Read,
    Propose,
    Apply,
}

public static class AssistantRoles
{
    /// <summary>The admin assistant: people in the web UI and on the Discord admin channel.</summary>
    public const string Admin = "admin";

    /// <summary>The player support agent: <c>!ask</c> in game and <c>/ask</c> on Discord.</summary>
    public const string Player = "player";

    public static readonly IReadOnlyList<string> All = [Admin, Player];
}

/// <summary>One agent role: its backends, operation modes, MCP servers and limits.</summary>
public sealed record RoleSettings
{
    /// <summary>Backend names in failover order.</summary>
    public List<string> Backends { get; init; } = [];

    /// <summary>Appended to the system prompt under "Notes from the admins". It is part of the cached prefix.</summary>
    public string RoleText { get; init; } = "";

    /// <summary>Mode per operation area; an area not listed uses the default (reads <c>read</c>, changes <c>propose</c>).</summary>
    public Dictionary<string, ToolMode> AreaModes { get; init; } = [];

    /// <summary>Mode per operation key (<c>pluginId/operationId</c>), over the area's.</summary>
    public Dictionary<string, ToolMode> OperationModes { get; init; } = [];

    /// <summary>Remote MCP servers (streamable HTTP) whose tools this role gets.</summary>
    public List<McpServerSettings> McpServers { get; init; } = [];

    public int MaxConcurrentRuns { get; init; } = 2;
    public int QueueWaitSeconds { get; init; } = 120;
    public int RunDeadlineSeconds { get; init; } = 300;
    public int MaxTurns { get; init; } = 20;

    /// <summary>The settings of a role nobody has saved yet.</summary>
    public static RoleSettings DefaultFor(string role) =>
        role == AssistantRoles.Player ? new() { QueueWaitSeconds = 60 } : new();
}

/// <summary>A remote MCP server. Its extra headers are secrets, stored apart like the API keys.</summary>
public sealed record McpServerSettings
{
    /// <summary>Short and unique per role; the tools are named <c>mcp_&lt;name&gt;_&lt;tool&gt;</c>.</summary>
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Enabled { get; init; } = true;

    /// <summary>The tools the admin marked read-only. Only these follow the read mode; every other tool of
    /// the server is always a proposal.</summary>
    public List<string> ReadOnlyTools { get; init; } = [];
}

public sealed record NotificationSettings
{
    /// <summary>Proposal cards in the conversation. The Approvals page is always there.</summary>
    public bool Cards { get; init; } = true;

    /// <summary>Web push to the admins who could approve a pending proposal or take a hand-over.</summary>
    public bool WebPush { get; init; } = true;

    /// <summary>A post in the admin Discord channel, with a link to the Approvals page.</summary>
    public bool Discord { get; init; }
}

public sealed record DiscordSettings
{
    /// <summary>The only channel where <c>/assistant</c> answers, and where notifications go.</summary>
    public string AdminChannelId { get; init; } = "";

    /// <summary>Quasar's address as admins open it, such as https://quasar.example.com, for links in posts.</summary>
    public string PublicUrl { get; init; } = "";
}

/// <summary>Per-player limits, keyed by Steam ID, or Discord ID for a user without a link.</summary>
public sealed record PlayerLimits
{
    public int RequestsPerHour { get; init; } = 10;
    public int RequestsPerDay { get; init; } = 30;

    /// <summary>Change tool calls that ran or became a proposal. Grid restores have their own limits.</summary>
    public int ActionsPerDay { get; init; } = 3;
}

public static class RestorePlacements
{
    public const string Original = "original";
    public const string NearPlayer = "near-player";
    public const string NearPlayerStation = "near-player-station";
    public static readonly IReadOnlyList<string> All = [Original, NearPlayer, NearPlayerStation];
}

/// <summary>How the player agent restores grids. The model only names the grid; these fill the rest.</summary>
public sealed record RestoreRules
{
    public string Placement { get; init; } = RestorePlacements.Original;
    public bool EmptyInventories { get; init; }
    public double ForeignExclusionMeters { get; init; } = 2000;
    public double MaxSearchMeters { get; init; } = 10000;

    /// <summary>Loss causes that restore without an admin when the tool's mode is <c>apply</c>.</summary>
    public List<string> AllowedCauses { get; init; } = ["destroyed", "cleanup"];

    /// <summary>Restores per player in the last 24 hours, 7 days and 30 days: 0 means none, null no limit.</summary>
    public int? PerDay { get; init; } = 1;
    public int? PerWeek { get; init; } = 2;
    public int? PerMonth { get; init; } = 4;

    public static readonly IReadOnlyList<string> Causes =
    [
        "destroyed",
        "ground",
        "deleted",
        "cleanup",
        "merged",
        "hangar",
        "unknown",
    ];
}

public sealed record PlayerSupportSettings
{
    public bool Enabled { get; init; }
    public PlayerLimits Limits { get; init; } = new();

    /// <summary>Shown on a player's first request and once a month after. Empty: a default naming the
    /// providers of the player role's backends.</summary>
    public string PrivacyNotice { get; init; } = "";

    /// <summary>Whether the player agent may read the Quasar book, the admins' handbook.</summary>
    public bool QuasarBook { get; init; }
    public RestoreRules Restore { get; init; } = new();
}

public sealed record AssistantSettings
{
    public List<BackendSettings> Backends { get; init; } = [];
    public Dictionary<string, RoleSettings> Roles { get; init; } =
        new() { [AssistantRoles.Admin] = new() };
    public int ResultCapBytes { get; init; } = 16 * 1024;
    public int ConversationRetentionDays { get; init; } = 30;
    public int ProposalExpiryDays { get; init; } = 30;
    public NotificationSettings Notifications { get; init; } = new();
    public DiscordSettings Discord { get; init; } = new();
    public PlayerSupportSettings Player { get; init; } = new();

    public RoleSettings Role(string role) =>
        Roles.GetValueOrDefault(role) ?? RoleSettings.DefaultFor(role);

    /// <summary>The role's backends in failover order; names without a backend are skipped.</summary>
    public IReadOnlyList<BackendSettings> BackendsOf(string role) =>
        Role(role)
            .Backends.Select(name => Backends.FirstOrDefault(backend => backend.Name == name))
            .OfType<BackendSettings>()
            .ToList();

    public AssistantSettings WithRole(string name, RoleSettings role) =>
        this with
        {
            Roles = new(Roles) { [name] = role },
        };
}
