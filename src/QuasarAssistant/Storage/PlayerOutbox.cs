using QuasarAssistant.Settings;

namespace QuasarAssistant.Storage;

public sealed record OutboxNote(DateTimeOffset Time, string Text);

/// <summary>
/// What a player is told on their next request, such as an admin's decision on their proposal: core has no way
/// to message a player outside a request. Kept in <c>player-outbox.json</c> by the player's key (<c>steam:&lt;id&gt;</c>
/// or <c>discord:&lt;id&gt;</c>), so it outlives a conversation that started over, until the proposal expiry.
/// </summary>
public sealed class PlayerOutbox
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, List<OutboxNote>> _notes;

    public PlayerOutbox(AssistantPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "player-outbox.json");
        _notes = JsonFiles.Read<Dictionary<string, List<OutboxNote>>>(_path) ?? [];
    }

    public void Add(string key, string text, DateTimeOffset time)
    {
        lock (_lock)
            Save(
                new(_notes)
                {
                    [key] = [.. _notes.GetValueOrDefault(key) ?? [], new OutboxNote(time, text)],
                }
            );
    }

    /// <summary>The player's notes, oldest first, and removes them.</summary>
    public IReadOnlyList<string> Take(string key)
    {
        lock (_lock)
        {
            if (!_notes.TryGetValue(key, out var notes))
                return [];
            var rest = new Dictionary<string, List<OutboxNote>>(_notes);
            rest.Remove(key);
            Save(rest);
            return notes.Select(note => note.Text).ToList();
        }
    }

    public void Forget(DateTimeOffset before)
    {
        lock (_lock)
        {
            if (!_notes.Values.Any(notes => notes.Any(note => note.Time < before)))
                return;
            Save(
                _notes
                    .Select(pair =>
                        (pair.Key, Notes: pair.Value.Where(n => n.Time >= before).ToList())
                    )
                    .Where(pair => pair.Notes.Count > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Notes)
            );
        }
    }

    private void Save(Dictionary<string, List<OutboxNote>> notes)
    {
        JsonFiles.Write(_path, notes);
        _notes = notes;
    }
}
