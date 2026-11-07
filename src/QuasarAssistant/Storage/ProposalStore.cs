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

/// <summary>All proposals in <c>proposals.json</c>; decided ones are kept as long as pending ones may be.</summary>
public sealed class ProposalStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly HashSet<string> _claimed = [];
    private List<Proposal> _proposals;

    public ProposalStore(AssistantPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "proposals.json");
        _proposals = JsonFiles.Read<List<Proposal>>(_path) ?? [];
    }

    public event Action? Changed;

    public IReadOnlyList<Proposal> All
    {
        get
        {
            lock (_lock)
                return _proposals;
        }
    }

    public IEnumerable<Proposal> Pending => All.Where(p => p.Status == ProposalStatus.Pending);

    public Proposal? Get(string id) => All.FirstOrDefault(p => p.Id == id);

    public void Add(Proposal proposal) => Change(list => [.. list, proposal]);

    /// <summary>Applies the change only while the proposal is still pending, so two admins can't both decide it.</summary>
    public Proposal? Decide(string id, Func<Proposal, Proposal> decide)
    {
        Proposal? decided = null;
        Change(list =>
            list.Select(p =>
                    p.Id == id && p.Status == ProposalStatus.Pending ? decided = decide(p) : p
                )
                .ToList()
        );
        return decided;
    }

    /// <summary>Claims a pending proposal for a decision in progress; false when someone else got it first.</summary>
    public bool TryClaim(string id)
    {
        lock (_lock)
            return Get(id)?.Status == ProposalStatus.Pending && _claimed.Add(id);
    }

    public void Release(string id)
    {
        lock (_lock)
            _claimed.Remove(id);
    }

    public void Forget(DateTimeOffset decidedBefore) =>
        Change(list =>
            list.Where(p => p.Status == ProposalStatus.Pending || p.Decided >= decidedBefore)
                .ToList()
        );

    private void Change(Func<List<Proposal>, List<Proposal>> change)
    {
        lock (_lock)
        {
            var changed = change(_proposals);
            JsonFiles.Write(_path, changed);
            _proposals = changed;
        }
        Changed?.Invoke();
    }
}
