using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Players;

/// <summary>One player's questions left, recent actions, and when they last got the privacy notice.</summary>
public sealed record PlayerUsage
{
    /// <summary>Questions left at <see cref="Refilled"/>; null: all of them.</summary>
    public int? Messages { get; init; }

    /// <summary>The last refill time; refills count whole periods from it.</summary>
    public DateTimeOffset? Refilled { get; init; }
    public List<DateTimeOffset> Actions { get; init; } = [];
    public DateTimeOffset? NoticeShown { get; init; }
}

/// <summary>
/// Per-player counters in <c>player-usage.json</c>, keyed by the conversation key (<c>steam:&lt;id&gt;</c>, or
/// <c>discord:&lt;id&gt;</c> without a link). Questions work as a refilling allowance: a player starts with the
/// maximum, each question uses one, and every refill period gives some back, never over the maximum. So a few
/// questions in a row are fine, but not a steady stream. Action times older than a day are dropped on each write.
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

    /// <summary>Uses one of the player's questions; null when there was one left, else the reply saying when the
    /// next comes back.</summary>
    public string? TryRequest(string key, PlayerLimits limits)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var usage = Get(key, now, limits);
            var left = usage.Messages ?? limits.MaxMessages;
            if (left <= 0)
            {
                if (limits.RefillMessages <= 0 || usage.Refilled is null)
                    return "You have no questions left.";
                var next = usage.Refilled!.Value.AddMinutes(limits.RefillMinutes);
                var minutes = (int)Math.Ceiling((next - now).TotalMinutes);
                return $"You have used up your questions. You get {limits.RefillMessages} more in {minutes} min, up to {limits.MaxMessages}.";
            }
            Save(
                key,
                usage with
                {
                    Messages = left - 1,
                    Refilled = usage.Refilled ?? now,
                },
                now,
                limits
            );
            return null;
        }
    }

    /// <summary>Questions the player has now.</summary>
    public int Left(string key, PlayerLimits limits)
    {
        lock (_lock)
            return Get(key, _time.GetUtcNow(), limits).Messages ?? limits.MaxMessages;
    }

    /// <summary>Counts an action; false when the daily limit is used up.</summary>
    public bool TryAction(string key, PlayerLimits limits)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var usage = Get(key, now, limits);
            if (usage.Actions.Count >= limits.ActionsPerDay)
                return false;
            Save(key, usage with { Actions = [.. usage.Actions, now] }, now, limits);
            return true;
        }
    }

    /// <summary>True on the first request and once a month after; marks the notice as shown.</summary>
    public bool TakeNoticeDue(string key)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var usage = Get(key, now, null);
            if (usage.NoticeShown is { } shown && shown > now - NoticeInterval)
                return false;
            Save(key, usage with { NoticeShown = now }, now, null);
            return true;
        }
    }

    /// <summary>The player's usage now: refills added for the whole periods passed, old actions dropped. Without
    /// limits the questions are left as stored.</summary>
    private PlayerUsage Get(string key, DateTimeOffset now, PlayerLimits? limits)
    {
        var usage = _usage.GetValueOrDefault(key) ?? new();
        usage = usage with { Actions = usage.Actions.Where(at => at > now.AddDays(-1)).ToList() };
        if (limits is null || usage is not { Messages: { } messages, Refilled: { } refilled })
            return usage;
        var period = TimeSpan.FromMinutes(Math.Max(1, limits.RefillMinutes));
        var periods = Math.Max(0, (long)((now - refilled) / period));
        var refilledTo = Math.Min(
            (long)messages + periods * limits.RefillMessages,
            limits.MaxMessages
        );
        // Full again: no partial period to keep.
        return refilledTo >= limits.MaxMessages
            ? usage with
            {
                Messages = null,
                Refilled = null,
            }
            : usage with
            {
                Messages = (int)refilledTo,
                Refilled = refilled + periods * period,
            };
    }

    private void Save(string key, PlayerUsage usage, DateTimeOffset now, PlayerLimits? limits)
    {
        // A player with nothing left to keep is dropped: no notice to remember, no recent action, and all
        // questions back. Without the limits at hand, players with questions in use are kept.
        var all = new Dictionary<string, PlayerUsage>(_usage) { [key] = usage };
        foreach (var (other, value) in _usage)
            if (
                other != key
                && (value.NoticeShown is not { } shown || shown <= now - NoticeInterval)
                && (
                    limits is null
                        ? value.Messages is null
                        : Get(other, now, limits).Messages is null
                )
                && Get(other, now, null).Actions.Count == 0
            )
                all.Remove(other);
        JsonFiles.Write(_path, all);
        _usage = all;
    }
}
