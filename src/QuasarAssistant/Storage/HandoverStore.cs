using System.Text.Json.Serialization;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Storage;

/// <summary>All hand-overs in <c>handovers.json</c>; handled ones go after the proposal expiry.</summary>
public sealed class HandoverStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<Handover> _items;

    public HandoverStore(AssistantPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "handovers.json");
        _items = JsonFiles.Read<List<Handover>>(_path) ?? [];
    }

    public event Action? Changed;

    public IReadOnlyList<Handover> All
    {
        get
        {
            lock (_lock)
                return _items;
        }
    }

    public IEnumerable<Handover> Open => All.Where(item => !item.Handled);

    public void Add(Handover handover) => Change(list => [.. list, handover]);

    /// <summary>False when it was handled already.</summary>
    public bool MarkHandled(string id, string by, DateTimeOffset at)
    {
        var done = false;
        Change(list =>
            list.Select(item =>
                {
                    if (item.Id != id || item.Handled)
                        return item;
                    done = true;
                    return item with { Handled = true, HandledBy = by, HandledAt = at };
                })
                .ToList()
        );
        return done;
    }

    public void Forget(DateTimeOffset handledBefore) =>
        Change(list =>
            list.Where(item => !item.Handled || item.HandledAt >= handledBefore).ToList()
        );

    private void Change(Func<List<Handover>, List<Handover>> change)
    {
        lock (_lock)
        {
            var changed = change(_items);
            JsonFiles.Write(_path, changed);
            _items = changed;
        }
        Changed?.Invoke();
    }
}
