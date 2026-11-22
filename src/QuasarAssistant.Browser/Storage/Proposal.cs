using System.Text.Json.Serialization;
using QuasarAssistant.Settings;
namespace QuasarAssistant.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<ProposalStatus>))]
public enum ProposalStatus
{
    Pending,
    Approved,
    Failed,
    Rejected,
    Expired,
}

/// <summary>The plugin's record of a proposal; core's audit entry (same ID) holds the arguments.</summary>
public sealed record Proposal
{
    /// <summary>The ID of core's <c>proposed</c> audit entry.</summary>
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string AskerSubject { get; init; }
    public string AskerName { get; init; } = "";

    /// <summary>The role that proposed it: <c>admin</c> or <c>player</c>.</summary>
    public string Role { get; init; } = AssistantRoles.Admin;
    public required string PluginId { get; init; }
    public required string OperationId { get; init; }

    /// <summary>The arguments as the model wrote them, redacted and shortened, for the card.</summary>
    public string Arguments { get; init; } = "";

    /// <summary>What the assistant said before the call, or why a restore needs an admin.</summary>
    public string? Reason { get; init; }

    /// <summary>Lines for the approval card, such as a restore's grid, cause, backup and placement.</summary>
    public List<string>? Details { get; init; }
    public DateTimeOffset Created { get; init; }
    public ProposalStatus Status { get; init; } = ProposalStatus.Pending;
    public string? DecidedBy { get; init; }
    public DateTimeOffset? Decided { get; init; }

    /// <summary>The result of the approved call, or the reason for a rejection.</summary>
    public string? Outcome { get; init; }

    public string Operation => PluginId + "/" + OperationId;
}
