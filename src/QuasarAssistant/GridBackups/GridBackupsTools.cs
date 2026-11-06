using System.Text.Json;
using System.Text.Json.Nodes;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Plugin.Abstractions.Servers;

namespace QuasarAssistant.GridBackups;

/// <summary>
/// The work behind the player's Grid Backups operations: which server a request means, the Grid Backups
/// companion calls and which node gets them. The Steam ID always comes from the player-agent principal.
/// </summary>
public sealed class GridBackupsTools(
    IQuasarServerQueries servers,
    IQuasarCompanionChannel companion
)
{
    /// <summary>Grid Backups' plugin ID (<c>GridBackups.xml</c>).</summary>
    public const string GridBackupsPluginId = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C";

    public const string PlacementOriginal = "original";
    public const string PlacementNearPlayer = "near-player";
    public const string PlacementNearPlayerStation = "near-player-station";
    public const double DefaultForeignExclusionMeters = 2000;
    public const double DefaultMaxSearchMeters = 10000;

    /// <summary>Leaves the restore operation's 300 s deadline some room to record the result.</summary>
    internal static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(280);

    /// <summary>A standalone server, or a cluster as one target with its regular nodes that have Grid Backups.</summary>
    internal sealed record Target(
        string UniqueName,
        string DisplayName,
        bool IsCluster,
        IReadOnlyList<QuasarServerNode> Nodes
    );

    /// <summary>The calling player agent; refuses anyone else, including a player agent without a Steam ID.</summary>
    internal static QuasarAgentIdentity Player(QuasarOperationContext context)
    {
        if (
            !context.User.IsQuasarAgent(out var agent)
            || agent.Kind != QuasarAgentKind.Player
            || !IsSteamId64(agent.PlayerSteamId)
        )
            throw new QuasarOperationException(
                403,
                "Only the player agent of a linked or in-game player can use this operation."
            );
        return agent;
    }

    internal static bool IsSteamId64(string? steamId) =>
        steamId is { Length: 17 } && steamId.All(char.IsAsciiDigit);

    private static Target WithGridBackups(QuasarServerTarget server) =>
        new(
            server.UniqueName,
            server.DisplayName,
            server.IsCluster,
            server.Nodes.Where(node => node.HasPlugin(GridBackupsPluginId)).ToList()
        );

    /// <summary>The named server, else the one the request came from, else the only one with Grid Backups.</summary>
    internal Target ResolveTarget(QuasarAgentIdentity player, string? uniqueName)
    {
        var targets = servers.GetServers().Select(WithGridBackups).ToList();
        var name = string.IsNullOrWhiteSpace(uniqueName) ? player.Server : uniqueName.Trim();
        if (name is not null)
        {
            if (name.StartsWith("cluster:", StringComparison.OrdinalIgnoreCase))
                name = name["cluster:".Length..];
            var target =
                targets.FirstOrDefault(candidate =>
                    string.Equals(candidate.UniqueName, name, StringComparison.OrdinalIgnoreCase)
                ) ?? throw new QuasarOperationException(404, $"Server '{name}' is not online.");
            if (target.Nodes.Count == 0)
                throw new QuasarOperationException(
                    409,
                    $"Grid Backups is not loaded on {target.DisplayName}, so its grid tools are not available there."
                );
            return target;
        }

        var candidates = targets.Where(target => target.Nodes.Count > 0).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new QuasarOperationException(
                409,
                "No online server has Grid Backups loaded."
            ),
            _ => throw new QuasarOperationException(
                400,
                "Name the server with uniqueName, one of: "
                    + string.Join(", ", candidates.Select(target => target.UniqueName))
                    + "."
            ),
        };
    }

    /// <summary>Grid Backups <c>list</c>. On a cluster the storage is shared, but <c>present</c> only sees the node
    /// asked, so every node is asked and a grid is present when any node has it.</summary>
    public async Task<JsonNode?> GridsAsync(
        QuasarAgentIdentity player,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName);
        var request = new { steamId = player.PlayerSteamId };
        var answers = await Task.WhenAll(
            target.Nodes.Select(async node =>
            {
                try
                {
                    return (
                        Answer: (JsonElement?)await CallAsync(node.AgentId, "list", request, token),
                        Error: (Exception?)null
                    );
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return (null, error);
                }
            })
        );
        var answered = answers
            .Where(answer => answer.Answer is not null)
            .Select(answer => answer.Answer!.Value)
            .ToList();
        if (answered.Count == 0)
            throw new QuasarOperationException(502, answers.First().Error!.Message);

        var result = JsonNode.Parse(answered[0].GetRawText())!;
        if (answered.Count > 1 && result["grids"] is JsonArray grids)
        {
            var present = answered
                .SelectMany(answer =>
                    answer.TryGetProperty("grids", out var list)
                        ? list.EnumerateArray()
                        : Enumerable.Empty<JsonElement>()
                )
                .Where(grid =>
                    grid.TryGetProperty("present", out var flag)
                    && flag.ValueKind == JsonValueKind.True
                )
                .Select(grid => grid.GetProperty("gridId").GetInt64())
                .ToHashSet();
            foreach (var grid in grids.OfType<JsonObject>())
                grid["present"] = grid["gridId"] is { } id && present.Contains(id.GetValue<long>());
        }

        if (answered.Count < answers.Length)
            result["presentIncomplete"] = true;
        return result;
    }

    public async Task<JsonElement> DescribeAsync(
        QuasarAgentIdentity player,
        long gridId,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName);
        return await CallAsync(
            target.Nodes[0].AgentId,
            "describe",
            new { steamId = player.PlayerSteamId, gridId },
            token
        );
    }

    public async Task<JsonElement> LossesAsync(
        QuasarAgentIdentity player,
        DateTimeOffset? sinceUtc,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName);
        return await CallAsync(
            target.Nodes[0].AgentId,
            "losses",
            new { steamId = player.PlayerSteamId, sinceUtc },
            token
        );
    }

    /// <summary>Grid Backups <c>restore</c> on the node that should spawn the grid. Throws 409 with
    /// <c>refused: reason</c> or <c>no-spot: reason</c> when nothing was restored.</summary>
    public async Task<JsonElement> RestoreAsync(
        QuasarAgentIdentity player,
        RestoreRequest request,
        QuasarUndoRecorder? undo,
        CancellationToken token
    )
    {
        var placement = (request.Placement ?? PlacementOriginal).Trim().ToLowerInvariant();
        if (
            placement
            is not (PlacementOriginal or PlacementNearPlayer or PlacementNearPlayerStation)
        )
            throw new QuasarOperationException(
                400,
                "Placement must be original, near-player or near-player-station."
            );

        var target = ResolveTarget(player, request.UniqueName);
        // Set before anything can refuse, so a refused restore names its server in the audit log.
        if (undo is not null)
            undo.Server = target.UniqueName;
        var exclusion = request.ForeignExclusionMeters ?? DefaultForeignExclusionMeters;
        var maxSearch = request.MaxSearchMeters ?? DefaultMaxSearchMeters;
        var node = await RestoreNodeAsync(
            target,
            player,
            request.GridId,
            placement,
            maxSearch,
            token
        );
        var answer = await CallAsync(
            node.AgentId,
            "restore",
            new
            {
                steamId = player.PlayerSteamId,
                gridId = request.GridId,
                placement,
                emptyInventories = request.EmptyInventories,
                foreignExclusionMeters = exclusion,
                maxSearchMeters = maxSearch,
            },
            token,
            RestoreTimeout
        );

        var status = answer.TryGetProperty("status", out var value) ? value.GetString() : null;
        if (status != "restored")
        {
            var reason = answer.TryGetProperty("reason", out var why) ? why.GetString() : null;
            throw new QuasarOperationException(
                409,
                $"{status ?? "refused"}: {reason ?? "Grid Backups gave no reason."}"
            );
        }

        undo?.SetData(
            new RestoreUndoData(
                target.UniqueName,
                target.IsCluster ? node.ClusterNode : null,
                answer.TryGetProperty("backupId", out var backup) ? backup.GetString() : null,
                request.GridId,
                answer.TryGetProperty("entityIds", out var ids)
                    ? ids.EnumerateArray().Select(id => id.GetInt64()).ToArray()
                    : []
            )
        );
        return answer;
    }

    /// <summary>Near-player modes run on the player's node. <c>original</c> on a cluster runs on the node
    /// that owns entities nearest the backup position, else the player's node, else any node.</summary>
    internal async Task<QuasarServerNode> RestoreNodeAsync(
        Target target,
        QuasarAgentIdentity player,
        long gridId,
        string placement,
        double maxSearchMeters,
        CancellationToken token
    )
    {
        var nodes = target.Nodes;
        if (!target.IsCluster)
            return nodes[0];

        var playerNode = Node(target, servers.FindPlayer(target.UniqueName, player.PlayerSteamId!));
        if (placement != PlacementOriginal)
            return playerNode
                ?? throw new QuasarOperationException(
                    409,
                    "refused: You need to be online on the cluster for a restore near you."
                );

        var describe = await CallAsync(
            nodes[0].AgentId,
            "describe",
            new { steamId = player.PlayerSteamId, gridId },
            token
        );
        if (BackupPosition(describe) is not { } position)
            return playerNode ?? nodes[0];

        string? owner;
        try
        {
            owner = await servers.FindPositionOwnerAsync(
                target.UniqueName,
                position.X,
                position.Y,
                position.Z,
                Math.Max(1, maxSearchMeters),
                token
            );
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            owner = null;
        }
        return Node(target, owner) ?? playerNode ?? nodes[0];
    }

    private static QuasarServerNode? Node(Target target, string? agentId) =>
        agentId is null ? null : target.Nodes.FirstOrDefault(node => node.AgentId == agentId);

    /// <summary>The position of the backup a restore would use: the newest taken before the latest loss.</summary>
    internal static (double X, double Y, double Z)? BackupPosition(JsonElement describe)
    {
        if (
            !describe.TryGetProperty("backups", out var backups)
            || backups.ValueKind != JsonValueKind.Array
        )
            return null;
        DateTimeOffset? lostAt =
            describe.TryGetProperty("loss", out var loss)
            && loss.ValueKind == JsonValueKind.Object
            && loss.TryGetProperty("timeUtc", out var time)
                ? time.GetDateTimeOffset()
                : null;
        foreach (var backup in backups.EnumerateArray())
        {
            if (
                lostAt is { } limit
                && backup.TryGetProperty("takenUtc", out var taken)
                && taken.GetDateTimeOffset() >= limit
            )
                continue;
            if (!backup.TryGetProperty("position", out var position))
                return null;
            return (
                position.GetProperty("x").GetDouble(),
                position.GetProperty("y").GetDouble(),
                position.GetProperty("z").GetDouble()
            );
        }

        return null;
    }

    internal Task<JsonElement> CallAsync(
        string agentId,
        string operation,
        object request,
        CancellationToken token,
        TimeSpan? timeout = null
    ) =>
        timeout is { } wait
            ? companion.SendAsync<object, JsonElement>(
                agentId,
                GridBackupsPluginId,
                operation,
                request,
                wait,
                token
            )
            : companion.SendAsync<object, JsonElement>(
                agentId,
                GridBackupsPluginId,
                operation,
                request,
                token
            );

    public sealed record RestoreRequest(
        long GridId,
        string? Placement = null,
        bool EmptyInventories = false,
        double? ForeignExclusionMeters = null,
        double? MaxSearchMeters = null,
        string? UniqueName = null
    );

    internal sealed record RestoreUndoData(
        string Server,
        string? Node,
        string? BackupId,
        long GridId,
        long[] EntityIds
    );
}
