using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Agent;

/// <summary>
/// The player's <c>my_grid_restore</c>. The model names the grid; the admin's restore rules fill in the placement
/// and distances. Over the player's quota nothing happens. A restore runs at once only in <c>apply</c> mode and
/// for a loss cause the admins allow; otherwise, or when Grid Backups finds no free spot, it becomes a proposal
/// with the reason. A refusal from Grid Backups goes back to the player, as does a grid with no backup of theirs.
/// </summary>
public sealed class GridRestores(
    IQuasarAuditLog audit,
    SettingsStore settings,
    ProposalService proposals,
    IEnumerable<QuasarOperation> operations,
    IServiceScopeFactory scopes,
    TimeProvider time
)
{
    private const string LossesKey = "quasar.core/my.grid.losses";
    private const string BackupsKey = "quasar.core/my.grid.backups";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<QuasarOperation> _operations = operations.ToList();

    /// <param name="Over">Why no restore is left, or null.</param>
    /// <param name="State">The player's use of each limit, for the proposal and the approval card.</param>
    public sealed record Quota(string? Over, string State);

    private sealed record Loss(string? Name, string Cause, DateTimeOffset? Time);

    public async Task<ToolOutcome> RunAsync(
        AgentTool tool,
        ClaimsPrincipal agent,
        JsonElement arguments,
        ToolCallContext run,
        CancellationToken token
    )
    {
        if (
            arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("gridId", out var id)
            || !id.TryGetInt64(out var gridId)
        )
            return new("Give the grid's gridId as a number.", true);
        if (!agent.IsQuasarAgent(out var player) || player.PlayerSteamId is not { } steamId)
            return new(
                "Refused: grid restores are for a player in game or with a linked Steam account.",
                true
            );

        var rules = settings.Current.Player.Restore;
        var quota = await QuotaAsync(steamId, rules, token);
        if (quota.Over is { } over)
            return new("No restore: " + over, true);

        var loss = await LossAsync(agent, gridId, token);
        var request = JsonSerializer.SerializeToElement(
            new
            {
                gridId,
                placement = rules.Placement,
                emptyInventories = rules.EmptyInventories,
                foreignExclusionMeters = rules.ForeignExclusionMeters,
                maxSearchMeters = rules.MaxSearchMeters,
            },
            Json
        );
        var why =
            tool.Mode != ToolMode.Apply || run.ProposeOnly
                ? "the server's rules send every restore to an admin"
            : loss is null ? "Grid Backups has no record of how the grid was lost"
            : !rules.AllowedCauses.Contains(loss.Cause)
                ? $"the grid was lost by \"{loss.Cause}\", which the rules don't restore without an admin"
            : null;

        if (why is null)
        {
            var result = await ToolExecutor.DispatchAsync(
                scopes,
                agent,
                tool.Operation!,
                request,
                token
            );
            if (result.Error is null)
                return new(
                    "Restored. " + ResultFilter.Cap(ResultFilter.Json(result.Value), run.CapBytes)
                );
            if (result.StatusCode == 409 && Reason(result.Error, "no-spot:") is { } noSpot)
                why =
                    "Grid Backups found no free spot ("
                    + noSpot
                    + "), so an admin can place it by hand";
            else if (result.StatusCode == 409 && Reason(result.Error, "refused:") is { } refused)
                return new("Refused: " + refused, true);
            else
                return new($"Refused or failed (HTTP {result.StatusCode}): {result.Error}", true);
        }

        // An admin can only restore a backup the player owns, so without one there is nothing to propose.
        var (backups, error) = await ReadAsync(agent, BackupsKey, new { gridId }, token);
        if (error is not null)
            return new("Refused: " + error, true);
        var details = Details(gridId, loss, backups, rules, quota);
        var proposal = await proposals.CreateAsync(
            agent,
            tool.Operation!,
            request,
            run.Conversation,
            $"Restore of grid {gridId} needs an admin: {why}. Restores used: {quota.State}.",
            token,
            details
        );
        return new(
            $"Proposal {proposal.Id} created: an admin decides on this restore because {why}. Nothing has changed yet.",
            false,
            proposal.Id
        );
    }

    /// <summary>
    /// Counts the player's restores from core's audit log: <c>ok</c> applied or approved
    /// <c>my.grid.restore</c> entries of their Steam ID in the last 24 hours, 7 days and 30 days, without those
    /// an admin undid, so an undo gives the restore back.
    /// </summary>
    public async Task<Quota> QuotaAsync(string steamId, RestoreRules rules, CancellationToken token)
    {
        var now = time.GetUtcNow();
        var entries = await audit.QueryAsync(
            new QuasarAuditFilter
            {
                From = now.AddDays(-30),
                Operation = ToolCatalog.RestoreKey,
                Result = QuasarAuditResult.Ok,
                Take = 1000,
            },
            token
        );
        var undone = entries.Select(entry => entry.Reverses).OfType<string>().ToHashSet();
        var restores = entries
            .Where(entry =>
                entry.Operation == ToolCatalog.RestoreKey
                && entry.Reverses is null
                && entry.Mode is QuasarAuditMode.Applied or QuasarAuditMode.Approved
                && entry.Requester.SteamId == steamId
                && !undone.Contains(entry.Id)
            )
            .Select(entry => entry.Time)
            .ToList();

        (string Period, int Days, int? Limit)[] periods =
        [
            ("24 hours", 1, rules.PerDay),
            ("7 days", 7, rules.PerWeek),
            ("30 days", 30, rules.PerMonth),
        ];
        string? over = null;
        List<string> state = [];
        foreach (var (period, days, limit) in periods)
        {
            var used = restores.Count(at => at > now.AddDays(-days));
            state.Add(
                limit is { } max ? $"{used} of {max} in {period}" : $"{used} in {period} (no limit)"
            );
            if (over is null && limit is { } cap && used >= cap)
                over =
                    cap == 0
                        ? $"grid restores are off for players here (none in {period})."
                        : $"the player had {used} restore{(used == 1 ? "" : "s")} in the last {period} and the limit is {cap}. Try again later.";
        }
        return new(over, string.Join(", ", state));
    }

    private async Task<Loss?> LossAsync(ClaimsPrincipal agent, long gridId, CancellationToken token)
    {
        var (losses, _) = await ReadAsync(agent, LossesKey, new { }, token);
        if (losses is not { } list || !list.TryGetProperty("losses", out var items))
            return null;
        foreach (var item in items.EnumerateArray())
            if (
                item.TryGetProperty("gridId", out var id)
                && id.TryGetInt64(out var value)
                && value == gridId
            )
                return new(
                    Str(item, "name"),
                    Str(item, "cause") ?? "unknown",
                    Str(item, "timeUtc") is { } at
                    && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null
                );
        return null;
    }

    /// <summary>What the approval card shows: the grid, the cause, the backup and the placement.</summary>
    private static List<string> Details(
        long gridId,
        Loss? loss,
        JsonElement? backups,
        RestoreRules rules,
        Quota quota
    )
    {
        var name = loss?.Name ?? (backups is { } b ? Str(b, "name") : null);
        string? backup = null;
        if (backups is { } found && found.TryGetProperty("backups", out var list))
            foreach (var item in list.EnumerateArray())
                if (
                    Str(item, "takenUtc") is { } taken
                    && DateTimeOffset.TryParse(taken, CultureInfo.InvariantCulture, out var at)
                    && (loss?.Time is not { } lost || at < lost)
                )
                {
                    backup = Stamp(at);
                    break; // Newest first.
                }
        return
        [
            $"Grid: {name ?? "unknown name"} ({gridId})",
            loss is null
                ? "Lost: no loss record"
                : $"Lost: {loss.Cause}" + (loss.Time is { } t ? $" at {Stamp(t)}" : ""),
            "Backup: " + (backup is null ? "none found before the loss" : "taken " + backup),
            $"Placement: {rules.Placement}, {rules.ForeignExclusionMeters:0} m clear of other players' grids, "
                + $"search up to {rules.MaxSearchMeters:0} m, inventories {(rules.EmptyInventories ? "emptied" : "kept")}",
            "Restores used: " + quota.State,
        ];
    }

    /// <returns>The answer, or why core or Grid Backups refused; neither when the operation isn't there.</returns>
    private async Task<(JsonElement? Value, string? Error)> ReadAsync(
        ClaimsPrincipal agent,
        string key,
        object request,
        CancellationToken token
    )
    {
        if (_operations.FirstOrDefault(op => op.Key == key) is not { } operation)
            return (null, null);
        QuasarOperationResult result;
        try
        {
            result = await ToolExecutor.DispatchAsync(
                scopes,
                agent,
                operation,
                JsonSerializer.SerializeToElement(request, Json),
                token
            );
        }
        // Core passes on what a handler throws, such as "The player is a big owner in no backup of this grid."
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (null, error.Message);
        }
        if (result.Error is not null)
            return (null, result.Error);
        return (
            result.Value is null ? null : JsonSerializer.SerializeToElement(result.Value, Json),
            null
        );
    }

    private static string? Reason(string error, string prefix) =>
        error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? error[prefix.Length..].Trim()
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
