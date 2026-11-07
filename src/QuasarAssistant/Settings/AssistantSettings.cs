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
    public const string Admin = "admin";

    // PR 6 adds "player" with its own backends, modes and limits.
}

/// <summary>One agent role: its backends, operation modes and limits.</summary>
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

    public int MaxConcurrentRuns { get; init; } = 2;
    public int QueueWaitSeconds { get; init; } = 120;
    public int RunDeadlineSeconds { get; init; } = 300;
    public int MaxTurns { get; init; } = 20;
}

public sealed record NotificationSettings
{
    /// <summary>Web push to the admins who could approve a pending proposal.</summary>
    public bool WebPush { get; init; } = true;

    /// <summary>A post in the admin Discord channel. PR 6 adds the channel; until then nothing is posted.</summary>
    public bool Discord { get; init; }
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

    public RoleSettings Role(string role) => Roles.GetValueOrDefault(role) ?? new();

    /// <summary>The role's backends in failover order; names without a backend are skipped.</summary>
    public IReadOnlyList<BackendSettings> BackendsOf(string role) =>
        Role(role)
            .Backends.Select(name => Backends.FirstOrDefault(backend => backend.Name == name))
            .OfType<BackendSettings>()
            .ToList();
}
