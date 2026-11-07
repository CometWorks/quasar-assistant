using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

/// <summary>The restore decision: quota, cause and mode, then what Grid Backups answered.</summary>
public sealed class RestoreTests
{
    private const BackendKind Kind = BackendKind.OpenAi;
    private const string SteamId = "76561198000000001";

    // The model asks for more than the grid; only gridId may pass.
    private const string GreedyArguments =
        "{\"gridId\":42,\"placement\":\"near-player\",\"foreignExclusionMeters\":0,\"maxSearchMeters\":99999,\"emptyInventories\":false,\"steamId\":\"76561198000000099\",\"uniqueName\":\"beta\"}";

    private sealed class Grid
    {
        public string? Cause { get; set; } = "destroyed";
        public Func<JsonElement, object?> Restore { get; set; } =
            _ => new { status = "restored", entityIds = new[] { 42 } };
        public Func<JsonElement, object?> Backups { get; set; } =
            _ => new
            {
                gridId = 42L,
                name = "Miner",
                backups = new[]
                {
                    new { takenUtc = "2026-11-05T11:30:00Z" },
                    new { takenUtc = "2026-11-05T10:00:00Z" },
                },
            };
    }

    private static List<QuasarOperation> Operations(Grid grid) =>
        [
            PlayerTests.PlayerRead(
                "my.grid.losses",
                _ => new
                {
                    losses = grid.Cause is null
                        ? Array.Empty<object>()
                        :
                        [
                            new
                            {
                                gridId = 42L,
                                name = "Miner",
                                cause = grid.Cause,
                                timeUtc = "2026-11-05T11:00:00Z",
                            },
                        ],
                }
            ),
            PlayerTests.PlayerRead("my.grid.backups", request => grid.Backups(request)),
            new TestOperation(
                "my.grid.restore",
                new QuasarOperationMetadata { Effect = QuasarOperationEffect.Change, Area = "my" },
                policies: [QuasarPolicyNames.CanActAsPlayer],
                pluginId: AssistantServiceCollectionExtensions.PluginId
            )
            {
                Handler = request => grid.Restore(request),
            },
        ];

    private static async Task<(Harness Harness, ManualTime Time)> StartAsync(
        Grid grid,
        ToolMode mode,
        Func<RestoreRules, RestoreRules>? rules = null
    )
    {
        var time = new ManualTime();
        var harness = await Harness.StartAsync(Operations(grid), time);
        harness.UsePlayerBackends(Kind, "p");
        harness.ConfigurePlayer(role =>
            role with
            {
                OperationModes = new() { [ToolCatalog.RestoreKey] = mode },
            }
        );
        harness.Configure(s =>
            s with
            {
                Player = s.Player with
                {
                    Restore = (rules ?? (r => r))(
                        s.Player.Restore with
                        {
                            Placement = RestorePlacements.NearPlayerStation,
                            ForeignExclusionMeters = 3000,
                        }
                    ),
                },
            }
        );
        return (harness, time);
    }

    private static async Task<ChatMessage> RestoreAsync(Harness harness)
    {
        harness.Server.Enqueue(
            "p",
            Replies.Answer(
                Kind,
                "On it.",
                [new ToolCall("c1", "my_grid_restore", GreedyArguments)]
            ),
            Replies.Answer(Kind, "Done.")
        );
        await harness.Players.HandleAsync(
            new PlayerAsk().Game("Bring back my Miner", SteamId),
            default
        );
        var owner = ConversationStore.PlayerPrefix + "steam:" + SteamId;
        var id = harness.Conversations.List(owner)[0].Id;
        return harness.Conversations.Get(owner, id)!.Messages.Last(m => m.Role == ChatRole.Tool);
    }

    private static List<(string Key, string Request, ClaimsPrincipal User)> Restores(
        Harness harness
    ) => harness.Dispatched.Calls.Where(call => call.Key == ToolCatalog.RestoreKey).ToList();

    [Fact]
    public async Task AllowedCauseInApplyIsDispatchedWithTheAdminsPlacement()
    {
        var (harness, _) = await StartAsync(new Grid(), ToolMode.Apply);
        await using var _h = harness;

        var result = await RestoreAsync(harness);

        Assert.StartsWith("Restored.", result.Text);
        var (_, request, user) = Assert.Single(Restores(harness));
        var sent = JsonDocument.Parse(request).RootElement;
        Assert.Equal(42, sent.GetProperty("gridId").GetInt64());
        Assert.Equal("near-player-station", sent.GetProperty("placement").GetString());
        Assert.Equal(3000, sent.GetProperty("foreignExclusionMeters").GetDouble());
        Assert.Equal(10000, sent.GetProperty("maxSearchMeters").GetDouble());
        Assert.False(sent.TryGetProperty("steamId", out _));
        Assert.False(sent.TryGetProperty("uniqueName", out _));
        Assert.True(user.IsQuasarAgent(out var agent));
        Assert.Equal(SteamId, agent.PlayerSteamId);
        Assert.Empty(harness.Audit.Proposals);
    }

    [Theory]
    [InlineData("deleted", ToolMode.Apply, "\"deleted\"")]
    [InlineData(null, ToolMode.Apply, "no record")]
    [InlineData("destroyed", ToolMode.Propose, "every restore")]
    public async Task OtherCauseOrProposeModeBecomesAProposal(
        string? cause,
        ToolMode mode,
        string why
    )
    {
        var (harness, _) = await StartAsync(new Grid { Cause = cause }, mode);
        await using var _h = harness;

        var result = await RestoreAsync(harness);

        Assert.Empty(Restores(harness));
        Assert.NotNull(result.ProposalId);
        Assert.Contains(why, result.Text);
        var (key, arguments, reason) = Assert.Single(harness.Audit.Proposals);
        Assert.Equal(ToolCatalog.RestoreKey, key);
        Assert.Contains("near-player-station", arguments);
        Assert.DoesNotContain("76561198000000099", arguments);
        Assert.Contains("0 of 1 in 24 hours", reason);
        var proposal = harness
            .Services.GetRequiredService<ProposalStore>()
            .Get(result.ProposalId!)!;
        Assert.Equal(AssistantRoles.Player, proposal.Role);
        Assert.Contains(proposal.Details!, line => line.StartsWith("Grid: Miner (42)"));
        Assert.Contains(
            proposal.Details!,
            line => line.StartsWith("Placement: near-player-station")
        );
        if (cause is not null)
            // The newest backup before the loss at 11:00, not the later one.
            Assert.Contains("Backup: taken 2026-11-05 10:00 UTC", proposal.Details!);
    }

    [Fact]
    public async Task QuotaCountsAuditEntriesWithoutTheUndoneOnes()
    {
        var (harness, time) = await StartAsync(new Grid(), ToolMode.Apply);
        await using var _h = harness;
        var earlier = harness.Audit.AddRestore(SteamId, time.Now.AddHours(-2));
        // Another player's restore and a pending proposal don't count.
        harness.Audit.AddRestore("76561198000000099", time.Now.AddHours(-1));
        harness.Audit.AddRestore(SteamId, time.Now.AddHours(-1), QuasarAuditMode.Proposed);

        var refused = await RestoreAsync(harness);
        Assert.True(refused.IsError);
        Assert.Contains("1 restore in the last 24 hours", refused.Text);
        Assert.Empty(Restores(harness));
        Assert.Empty(harness.Audit.Proposals);

        harness.Audit.AddUndo(earlier, time.Now.AddMinutes(-5));
        var restored = await RestoreAsync(harness);
        Assert.StartsWith("Restored.", restored.Text);
    }

    [Fact]
    public async Task WeekAndMonthLimitsCount()
    {
        var (harness, time) = await StartAsync(new Grid(), ToolMode.Apply);
        await using var _h = harness;
        harness.Audit.AddRestore(SteamId, time.Now.AddDays(-3));
        harness.Audit.AddRestore(SteamId, time.Now.AddDays(-5), QuasarAuditMode.Approved);

        var result = await RestoreAsync(harness);

        Assert.Contains("in the last 7 days and the limit is 2", result.Text);
    }

    [Fact]
    public async Task ZeroMeansNoneAndEmptyMeansNoLimit()
    {
        var (none, _) = await StartAsync(new Grid(), ToolMode.Apply, r => r with { PerDay = 0 });
        await using (none)
        {
            var result = await RestoreAsync(none);
            Assert.Contains("restores are off", result.Text);
            Assert.Empty(Restores(none));
        }

        var (open, time) = await StartAsync(
            new Grid(),
            ToolMode.Apply,
            r => r with { PerDay = null, PerWeek = null, PerMonth = null }
        );
        await using (open)
        {
            for (var i = 0; i < 6; i++)
                open.Audit.AddRestore(SteamId, time.Now.AddHours(-i - 1));
            var result = await RestoreAsync(open);
            Assert.StartsWith("Restored.", result.Text);
        }
    }

    [Fact]
    public async Task NoSpotBecomesAProposal()
    {
        var grid = new Grid
        {
            Restore = _ =>
                throw new QuasarOperationException(
                    409,
                    "no-spot: Every spot within 10000 m is taken."
                ),
        };
        var (harness, _) = await StartAsync(grid, ToolMode.Apply);
        await using var _h = harness;

        var result = await RestoreAsync(harness);

        Assert.Single(Restores(harness));
        Assert.NotNull(result.ProposalId);
        var (_, _, reason) = Assert.Single(harness.Audit.Proposals);
        Assert.Contains("no free spot (Every spot within 10000 m is taken.)", reason);
    }

    [Fact]
    public async Task RefusedIsToldWithoutAProposal()
    {
        var grid = new Grid
        {
            Restore = _ =>
                throw new QuasarOperationException(409, "refused: The grid is still in the world."),
        };
        var (harness, _) = await StartAsync(grid, ToolMode.Apply);
        await using var _h = harness;

        var result = await RestoreAsync(harness);

        Assert.True(result.IsError);
        Assert.Equal("Refused: The grid is still in the world.", result.Text);
        Assert.Empty(harness.Audit.Proposals);
    }

    // Core passes the companion's answer on as an exception, as for a grid the player never owned.
    private static object? NeverOwned(JsonElement _) =>
        throw new InvalidOperationException("The player is a big owner in no backup of this grid.");

    [Theory]
    [InlineData(ToolMode.Apply)]
    [InlineData(ToolMode.Propose)]
    public async Task AGridWithoutABackupOfTheirsIsRefusedWithoutAProposal(ToolMode mode)
    {
        var (harness, _) = await StartAsync(new Grid { Cause = null, Backups = NeverOwned }, mode);
        await using var _h = harness;

        var result = await RestoreAsync(harness);

        Assert.True(result.IsError);
        Assert.Equal("Refused: The player is a big owner in no backup of this grid.", result.Text);
        Assert.Null(result.ProposalId);
        Assert.Empty(harness.Audit.Proposals);
        Assert.Empty(harness.Services.GetRequiredService<HandoverStore>().Open);
        // The model got the refusal and answered the player.
        var owner = ConversationStore.PlayerPrefix + "steam:" + SteamId;
        var conversation = harness.Conversations.Get(
            owner,
            harness.Conversations.List(owner)[0].Id
        )!;
        Assert.Equal("Done.", conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task AThrowingRestoreComesBackAsAToolError()
    {
        var grid = new Grid
        {
            Restore = _ => throw new InvalidOperationException("The companion is gone."),
        };
        var (harness, _) = await StartAsync(grid, ToolMode.Apply);
        await using var _h = harness;

        var result = await RestoreAsync(harness);

        Assert.True(result.IsError);
        Assert.Equal("Failed: The companion is gone.", result.Text);
        Assert.Empty(harness.Services.GetRequiredService<HandoverStore>().Open);
    }

    [Fact]
    public async Task TheModelOnlySeesTheGridId()
    {
        var (harness, _) = await StartAsync(new Grid(), ToolMode.Apply);
        await using var _h = harness;
        harness.Server.Enqueue("p", Replies.Answer(Kind, "Hi"));

        await harness.Players.HandleAsync(new PlayerAsk().Game("hi", SteamId), default);

        var tool = harness
            .Server.BodiesOf("p")[0]
            .GetProperty("tools")
            .EnumerateArray()
            .Single(t =>
                t.GetProperty("function").GetProperty("name").GetString() == "my_grid_restore"
            );
        var properties = tool.GetProperty("function")
            .GetProperty("parameters")
            .GetProperty("properties");
        Assert.Equal(["gridId"], properties.EnumerateObject().Select(p => p.Name));
    }
}
