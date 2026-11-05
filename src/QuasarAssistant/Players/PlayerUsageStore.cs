using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Players;

/// <summary>One player's recent requests and actions, and when they last got the privacy notice.</summary>
public sealed record PlayerUsage
{
    public List<DateTimeOffset> Requests { get; init; } = [];
    public List<DateTimeOffset> Actions { get; init; } = [];
    public DateTimeOffset? NoticeShown { get; init; }
}

/// <summary>
/// Per-player counters in <c>player-usage.json</c>, keyed by the conversation key (<c>steam:&lt;id&gt;</c>, or
/// <c>discord:&lt;id&gt;</c> without a link). Times older than a day are dropped on each write.
/// </summary>
public sealed class PlayerUsageStore
{
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromDays(30);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private Dictionary<string, PlayerUsage> _usage;

    public PlayerUsageStore(AssistantPaths paths, TimeProvider time)
    {
        _path = Path.Combine(paths.DataDirectory, "player-usage.json");
        _time = time;
        _usage = JsonFiles.Read<Dictionary<string, PlayerUsage>>(_path) ?? [];
    }

    /// <summary>Counts a request; null when it is within the limits, else the reply that says which one was hit.</summary>
    public string? TryRequest(string key, PlayerLimits limits)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var usage = Get(key, now);
            if (usage.Requests.Count(at => at > now.AddHours(-1)) >= limits.RequestsPerHour)
                return $"You have asked {limits.RequestsPerHour} times in the last hour, the most allowed. Try again later.";
            if (usage.Requests.Count >= limits.RequestsPerDay)
                return $"You have asked {limits.RequestsPerDay} times in the last 24 hours, the most allowed. Try again tomorrow.";
            Save(key, usage with { Requests = [.. usage.Requests, now] }, now);
            return null;
        }
    }

    /// <summary>Counts an action; false when the daily limit is used up.</summary>
    public bool TryAction(string key, PlayerLimits limits)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var usage = Get(key, now);
            if (usage.Actions.Count >= limits.ActionsPerDay)
                return false;
            Save(key, usage with { Actions = [.. usage.Actions, now] }, now);
            return true;
        }
    }

    /// <summary>True on the first request and once a month after; marks the notice as shown.</summary>
    public bool TakeNoticeDue(string key)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var usage = Get(key, now);
            if (usage.NoticeShown is { } shown && shown > now - NoticeInterval)
                return false;
            Save(key, usage with { NoticeShown = now }, now);
            return true;
        }
    }

    private PlayerUsage Get(string key, DateTimeOffset now)
    {
        var usage = _usage.GetValueOrDefault(key) ?? new();
        var since = now.AddDays(-1);
        return usage with
        {
            Requests = usage.Requests.Where(at => at > since).ToList(),
            Actions = usage.Actions.Where(at => at > since).ToList(),
        };
    }

    private void Save(string key, PlayerUsage usage, DateTimeOffset now)
    {
        // A player kept only for the notice needs no counters; one with nothing left is dropped.
        var all = new Dictionary<string, PlayerUsage>(_usage) { [key] = usage };
        foreach (var (other, value) in _usage)
            if (value.NoticeShown is not { } shown || shown <= now - NoticeInterval)
                if (Get(other, now) is { Requests.Count: 0, Actions.Count: 0 } && other != key)
                    all.Remove(other);
        JsonFiles.Write(_path, all);
        _usage = all;
    }
}
