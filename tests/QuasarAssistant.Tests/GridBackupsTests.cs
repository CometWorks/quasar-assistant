using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.GridBackups;

namespace QuasarAssistant.Tests;

/// <summary>The player's Grid Backups operations, through core's dispatcher (faked) with fake server queries and
/// a fake companion channel.</summary>
public sealed class GridBackupsTests : IAsyncLifetime
{
    private const string Pilot = "76561198000000001";
    private const string Stranger = "76561198000000999";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] MyOperations =
    [
        "my.grids",
        "my.grid.backups",
        "my.grid.losses",
        "my.grid.restore",
    ];

    private Harness _harness = null!;
    private FakeServers Servers => _harness.Services.GetRequiredService<FakeServers>();
    private FakeCompanion Companion => _harness.Services.GetRequiredService<FakeCompanion>();
    private readonly string _blobs = Path.Combine(
        Path.GetTempPath(),
        "quasar-assistant-tests",
        Guid.NewGuid().ToString("N")
    );

    public async Task InitializeAsync() => _harness = await Harness.StartAsync([]);

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public void MyOperationsDescribeThemselvesAndExportTheirRequestSchema()
    {
        var operations = _harness
            .Services.GetServices<QuasarOperation>()
            .Where(operation =>
                operation.PluginId == AssistantServiceCollectionExtensions.PluginId
                && operation.OperationId.StartsWith("my.", StringComparison.Ordinal)
            )
            .ToList();
        Assert.Equal(MyOperations.Order(), operations.Select(op => op.OperationId).Order());
        foreach (var operation in operations)
        {
            Assert.Equal(new[] { QuasarPolicyNames.CanActAsPlayer }, operation.DefaultPolicies);
            Assert.Equal("my", operation.Metadata.Area);
            Assert.False(
                string.IsNullOrWhiteSpace(operation.Metadata.Description),
                operation.OperationId
            );
            var schema = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(
                JsonSerializerOptions.Web,
                operation.RequestType!
            );
            Assert.NotNull(schema["properties"]?["uniqueName"]);
        }

        var restore = operations.Single(op => op.OperationId == "my.grid.restore").Metadata;
        Assert.Equal(QuasarOperationEffect.Change, restore.Effect);
        Assert.Equal(QuasarReversal.Undo, restore.Reversal);
        Assert.Equal(QuasarOperationMetadata.MaximumDeadline, restore.Deadline);
    }

    [Fact]
    public async Task MyOperationsAreOnlyForAPlayerAgentWithASteamId()
    {
        Servers.Standalone("alpha");
        var admin = Harness.Person();
        var assistant = new ClaimsPrincipal(
            PlayerAsk.Identity(QuasarAgentKind.Assistant, admin, null, null, "c")
        );
        var unlinked = new ClaimsPrincipal(
            PlayerAsk.Identity(QuasarAgentKind.Player, null, null, null, "discord:999", null)
        );
        foreach (var operation in MyOperations)
        {
            Assert.Equal(403, (await CallAsync(admin, operation, new { gridId = 1 })).StatusCode);
            Assert.Equal(
                403,
                (await CallAsync(assistant, operation, new { gridId = 1 })).StatusCode
            );
            var refused = await CallAsync(unlinked, operation, new { gridId = 1 });
            Assert.Equal(403, refused.StatusCode);
            Assert.Contains("player agent", refused.Error);
        }
        Assert.Empty(Companion.Calls);

        Assert.Equal(200, (await CallAsync(Player("alpha"), "my.grids")).StatusCode);
    }

    [Fact]
    public async Task CompanionRequestsAlwaysCarryTheCallersOwnSteamId()
    {
        Servers.Standalone("alpha");
        Companion.Answer = (_, operation, _) =>
            operation switch
            {
                "list" => new { grids = new[] { new { gridId = 7L, present = false } } },
                _ => new { ok = true },
            };

        var player = Player("alpha");
        Assert.Equal(
            200,
            (await CallAsync(player, "my.grids", new { steamId = Stranger })).StatusCode
        );
        Assert.Equal(
            200,
            (
                await CallAsync(player, "my.grid.backups", new { gridId = 7, steamId = Stranger })
            ).StatusCode
        );
        Assert.Equal(
            200,
            (await CallAsync(player, "my.grid.losses", new { steamId = Stranger })).StatusCode
        );
        var calls = Companion.Calls.ToList();
        Assert.Equal(["list", "describe", "losses"], calls.Select(call => call.Operation));
        Assert.All(
            calls,
            call =>
            {
                Assert.Equal(GridBackupsTools.GridBackupsPluginId, call.PluginId);
                Assert.Equal("alpha:1", call.AgentId);
                Assert.Equal(Pilot, call.Request.GetProperty("steamId").GetString());
            }
        );
        Assert.Equal(7, calls[1].Request.GetProperty("gridId").GetInt64());
    }

    [Fact]
    public async Task TheServerDefaultsToTheRequestsSourceAndOthersMustBeNamed()
    {
        Servers.Standalone("alpha");
        Servers.Standalone("beta");
        Servers.Standalone("gamma", gridBackups: false);
        Companion.Answer = (agentId, _, _) =>
            new { grids = Array.Empty<object>(), server = agentId.Split(':')[0] };

        Assert.Equal(
            "alpha",
            Json(await CallAsync(Player("alpha"), "my.grids")).GetProperty("server").GetString()
        );
        var discordPlayer = Player(null);
        var ambiguous = await CallAsync(discordPlayer, "my.grids");
        Assert.Equal(400, ambiguous.StatusCode);
        Assert.Contains("alpha, beta", ambiguous.Error);
        Assert.DoesNotContain("gamma", ambiguous.Error);
        Assert.Equal(
            "beta",
            Json(await CallAsync(discordPlayer, "my.grids", new { uniqueName = "beta" }))
                .GetProperty("server")
                .GetString()
        );
        Assert.Equal(
            409,
            (await CallAsync(discordPlayer, "my.grids", new { uniqueName = "gamma" })).StatusCode
        );
        Assert.Equal(
            404,
            (await CallAsync(discordPlayer, "my.grids", new { uniqueName = "delta" })).StatusCode
        );
    }

    [Fact]
    public async Task RestoreIsRecordedAndItsUndoRemovesTheRestoredGrids()
    {
        Servers.Standalone("alpha");
        var removed = new List<long[]>();
        var answer = "restored";
        Companion.Answer = (agentId, operation, request) =>
        {
            if (operation == "remove-restored")
            {
                var ids = request
                    .GetProperty("entityIds")
                    .EnumerateArray()
                    .Select(id => id.GetInt64())
                    .ToArray();
                removed.Add(ids);
                foreach (var id in ids)
                    Servers.Grids.TryRemove(id, out _);
                return new { removed = ids, missing = Array.Empty<long>() };
            }
            if (answer != "restored")
                return new { status = answer, reason = "No free space." };
            Servers.Grids[10] = agentId;
            Servers.Grids[11] = agentId;
            return new
            {
                status = "restored",
                backupId = "b/1.sbc",
                entityIds = new[] { 10L, 11L },
            };
        };

        var undo = new QuasarUndoRecorder(_blobs);
        var restored = await CallAsync(
            Player("alpha"),
            "my.grid.restore",
            new { gridId = 10, steamId = Stranger },
            undo
        );
        Assert.Equal(200, restored.StatusCode);
        Assert.Equal("restored", Json(restored).GetProperty("status").GetString());
        var call = Assert.Single(Companion.Calls);
        Assert.Equal("restore", call.Operation);
        Assert.Equal(GridBackupsTools.RestoreTimeout, call.Timeout);
        Assert.Equal(Pilot, call.Request.GetProperty("steamId").GetString());
        Assert.Equal("original", call.Request.GetProperty("placement").GetString());
        Assert.Equal(2000, call.Request.GetProperty("foreignExclusionMeters").GetDouble());
        Assert.Equal(10000, call.Request.GetProperty("maxSearchMeters").GetDouble());
        Assert.Equal("alpha", undo.Server);
        Assert.Equal(
            new long[] { 10, 11 },
            undo.Data!.Value.GetProperty("entityIds").EnumerateArray().Select(id => id.GetInt64())
        );

        // Core runs the undo for an admin; here the handler is called directly.
        var handler = Operation("my.grid.restore").Metadata.Undo!;
        var context = new QuasarUndoContext(
            _harness.Services,
            Harness.Person(),
            undo.Data.Value,
            _blobs
        );
        Assert.Null(await handler.CheckAsync(context, default));
        Servers.Grids.TryRemove(11, out _);
        var conflict = await handler.CheckAsync(context, default);
        Assert.Contains("11", conflict);
        await handler.ReverseAsync(context, default);
        Assert.Equal(new long[] { 10 }, Assert.Single(removed));
        Assert.Empty(Servers.Grids);

        answer = "no-spot";
        var refusedUndo = new QuasarUndoRecorder(_blobs);
        var noSpot = await CallAsync(
            Player("alpha"),
            "my.grid.restore",
            new { gridId = 10 },
            refusedUndo
        );
        Assert.Equal(409, noSpot.StatusCode);
        Assert.StartsWith("no-spot: No free space.", noSpot.Error);
        // A refused restore still names its server for the audit log.
        Assert.Equal("alpha", refusedUndo.Server);
        Assert.Null(refusedUndo.Data);
        Assert.Equal(
            400,
            (
                await CallAsync(
                    Player("alpha"),
                    "my.grid.restore",
                    new { gridId = 10, placement = "orbit" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ClusterRestoreGoesToTheNodeNearTheBackupOrThePlayer()
    {
        // Core leaves the World Authority out of the nodes.
        var nodes = Servers.Cluster("fleet", "a", "b");
        var (nodeA, nodeB) = (nodes[0], nodes[1]);
        Servers.Players[Pilot] = nodeA;
        Servers.PositionOwner = _ => nodeB;
        var restoredOn = new List<string>();
        Companion.Answer = (agentId, operation, _) =>
        {
            if (operation == "restore")
                restoredOn.Add(agentId);
            return operation switch
            {
                "list" => new { grids = new[] { new { gridId = 7L, present = agentId == nodeB } } },
                "describe" => new
                {
                    gridId = 7L,
                    backups = new object[]
                    {
                        new
                        {
                            takenUtc = "2026-11-05T12:00:00Z",
                            position = new
                            {
                                x = 9.0,
                                y = 9.0,
                                z = 9.0,
                            },
                        },
                        new
                        {
                            takenUtc = "2026-11-05T10:00:00Z",
                            position = new
                            {
                                x = 1.0,
                                y = 2.0,
                                z = 3.0,
                            },
                        },
                    },
                    loss = new { timeUtc = "2026-11-05T11:00:00Z", cause = "destroyed" },
                },
                _ => new { status = "restored", entityIds = new[] { 7L } },
            };
        };

        var player = Player("fleet");
        var undo = new QuasarUndoRecorder(_blobs);
        Assert.Equal(
            200,
            (await CallAsync(player, "my.grid.restore", new { gridId = 7 }, undo)).StatusCode
        );
        Assert.Equal("node-b", undo.Data!.Value.GetProperty("node").GetString());
        Assert.Equal(
            200,
            (
                await CallAsync(
                    player,
                    "my.grid.restore",
                    new { gridId = 7, placement = "near-player" }
                )
            ).StatusCode
        );
        Assert.Equal([nodeB, nodeA], restoredOn);
        var position = Assert.Single(Servers.PositionQueries);
        Assert.Equal((1.0, 2.0, 3.0), (position.X, position.Y, position.Z)); // the backup before the loss
        Assert.Equal(10000, position.Radius);

        var grids = Json(await CallAsync(player, "my.grids"));
        Assert.True(grids.GetProperty("grids")[0].GetProperty("present").GetBoolean());

        // Nobody owns the area: the player's node.
        Servers.PositionOwner = _ => null;
        await CallAsync(player, "my.grid.restore", new { gridId = 7 });
        Assert.Equal(nodeA, restoredOn[^1]);

        // A restore near the player needs them online on the cluster.
        Servers.Players.Clear();
        var offline = await CallAsync(
            player,
            "my.grid.restore",
            new { gridId = 7, placement = "near-player" }
        );
        Assert.Equal(409, offline.StatusCode);
        Assert.StartsWith("refused:", offline.Error);
    }

    private static ClaimsPrincipal Player(string? server) => PlayerAsk.Agent(Pilot, server: server);

    private QuasarOperation Operation(string operationId) =>
        _harness
            .Services.GetServices<QuasarOperation>()
            .Single(op =>
                op.PluginId == AssistantServiceCollectionExtensions.PluginId
                && op.OperationId == operationId
            );

    private static JsonElement Json(QuasarOperationResult result)
    {
        Assert.True(result.Error is null, result.Error);
        return JsonSerializer.SerializeToElement(result.Value, Web);
    }

    private async Task<QuasarOperationResult> CallAsync(
        ClaimsPrincipal user,
        string operationId,
        object? request = null,
        QuasarUndoRecorder? undo = null
    )
    {
        await using var scope = _harness.Services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<IQuasarOperationDispatcher>()
            .DispatchAsync(
                new(user, scope.ServiceProvider) { Undo = undo },
                AssistantServiceCollectionExtensions.PluginId,
                operationId,
                JsonSerializer.SerializeToElement(request ?? new { }),
                default
            );
    }
}
