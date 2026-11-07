using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Monitoring;

/// <summary>One model run on an incident, or a run skipped for the budget.</summary>
/// <param name="Tokens">Uncached input and output tokens.</param>
public sealed record IncidentRun(
    DateTimeOffset Time,
    string Outcome,
    string Answer,
    long Tokens,
    double? Usd,
    List<string> Tools
);

/// <summary>A problem the checks found, from when it appeared until it went away.</summary>
public sealed record Incident
{
    public required string Id { get; init; }

    /// <summary>The check's key, such as <c>down:alpha</c>; the same key is the same problem.</summary>
    public required string Key { get; init; }
    public string Text { get; init; } = "";
    public string? Server { get; init; }
    public DateTimeOffset Opened { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public DateTimeOffset? Resolved { get; init; }
    public required string ConversationId { get; init; }
    public List<IncidentRun> Runs { get; init; } = [];
}

/// <summary>Incidents in <c>monitor.json</c>, and the budget period the admins were last told about.</summary>
public sealed class IncidentStore
{
    private sealed record State
    {
        public List<Incident> Incidents { get; init; } = [];
        public DateTimeOffset? BudgetNoticePeriod { get; init; }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private State _state;

    public IncidentStore(AssistantPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "monitor.json");
        _state = JsonFiles.Read<State>(_path) ?? new();
    }

    public event Action? Changed;

    public IReadOnlyList<Incident> All
    {
        get
        {
            lock (_lock)
                return _state.Incidents;
        }
    }

    public Incident? OpenFor(string key) =>
        All.FirstOrDefault(incident => incident.Key == key && incident.Resolved is null);

    public Incident? Get(string id) => All.FirstOrDefault(incident => incident.Id == id);

    public void Add(Incident incident) =>
        Change(state => state with { Incidents = [.. state.Incidents, incident] });

    public void Update(string id, Func<Incident, Incident> change) =>
        Change(state =>
            state with
            {
                Incidents = state.Incidents.Select(i => i.Id == id ? change(i) : i).ToList(),
            }
        );

    /// <summary>True once per budget period: the first time the budget runs out in it.</summary>
    public bool TryMarkBudgetNotice(DateTimeOffset period)
    {
        var first = false;
        Change(state =>
        {
            first = state.BudgetNoticePeriod != period;
            return state with { BudgetNoticePeriod = period };
        });
        return first;
    }

    public DateTimeOffset? BudgetNoticePeriod
    {
        get
        {
            lock (_lock)
                return _state.BudgetNoticePeriod;
        }
    }

    /// <summary>Drops incidents resolved before the time.</summary>
    public void Forget(DateTimeOffset before) =>
        Change(state =>
            state with
            {
                Incidents = state
                    .Incidents.Where(i => i.Resolved is null || i.Resolved >= before)
                    .ToList(),
            }
        );

    private void Change(Func<State, State> change)
    {
        lock (_lock)
        {
            _state = change(_state);
            JsonFiles.Write(_path, _state);
        }
        Changed?.Invoke();
    }
}
