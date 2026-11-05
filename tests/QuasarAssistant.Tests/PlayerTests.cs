using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Players;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

public sealed class PlayerTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    public static TestOperation PlayerRead(string id, Func<JsonElement, object?>? handler = null) =>
        new(
            id,
            new QuasarOperationMetadata { Effect = QuasarOperationEffect.Read, Area = "my" },
            policies: [QuasarPolicyNames.CanActAsPlayer]
        )
        {
            Handler = handler ?? (_ => new { ok = true }),
        };

    public static List<QuasarOperation> Operations() =>
        [
            PlayerRead(
                "my.grids",
                _ => new { grids = new[] { new { gridId = 42, name = "Miner" } } }
            ),
            PlayerRead("my.server.info"),
            new TestOperation(
                "my.grid.restore",
                new QuasarOperationMetadata { Effect = QuasarOperationEffect.Change, Area = "my" },
                policies: [QuasarPolicyNames.CanActAsPlayer]
            ),
            TestOperation.Read("servers.status"),
        ];

    public static IReadOnlyList<string> ToolNames(JsonElement body) =>
        body.TryGetProperty("tools", out var tools)
            ? tools
                .EnumerateArray()
                .Select(tool =>
                    tool.TryGetProperty("function", out var function)
                        ? function.GetProperty("name").GetString()!
                        : tool.GetProperty("name").GetString()!
                )
                .ToList()
            : [];

    private static async Task<Harness> StartAsync(ManualTime? time = null)
    {
        var harness = await Harness.StartAsync(Operations(), time ?? new ManualTime());
        harness.UsePlayerBackends(Kind, "p");
        return harness;
    }

    [Fact]
    public async Task DisabledGetsAFixedReplyWithoutAModelRun()
    {
        await using var harness = await Harness.StartAsync(Operations());
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(ask.Game("Where is my ship?"), default);

        Assert.Equal([PlayerRequestHandler.NotEnabled], ask.Replies);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task HourAndDayLimitsGiveAFixedReply()
    {
        var time = new ManualTime();
        await using var harness = await StartAsync(time);
        harness.Configure(s =>
            s with
            {
                Player = s.Player with
                {
                    Limits = new() { RequestsPerHour = 2, RequestsPerDay = 3 },
                },
            }
        );
        for (var i = 0; i < 3; i++)
            harness.Server.Enqueue("p", Replies.Answer(Kind, "ok " + i));
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(ask.Game("one"), default);
        await harness.Players.HandleAsync(ask.Game("two"), default);
        await harness.Players.HandleAsync(ask.Game("three"), default);
        Assert.Contains("in the last hour", ask.Replies[^1]);
        Assert.Equal(2, harness.Server.Requests.Count);

        time.Now = time.Now.AddHours(2);
        await harness.Players.HandleAsync(ask.Game("four"), default);
        Assert.Equal("ok 2", ask.Replies[^1]);
        await harness.Players.HandleAsync(ask.Game("five"), default);
        Assert.Contains("in the last 24 hours", ask.Replies[^1]);
        Assert.Equal(3, harness.Server.Requests.Count);

        // Another player has their own counters.
        harness.Server.Enqueue("p", Replies.Answer(Kind, "hello"));
        await harness.Players.HandleAsync(ask.Game("hi", "76561198000000002"), default);
        Assert.Equal("hello", ask.Replies[^1]);
    }

    [Fact]
    public async Task ActionLimitRefusesFurtherChanges()
    {
        await using var harness = await StartAsync();
        harness.Configure(s =>
            s with
            {
                Player = s.Player with { Limits = new() { ActionsPerDay = 1 } },
            }
        );
        harness.Server.Enqueue(
            "p",
            Replies.Answer(
                Kind,
                "",
                [
                    new ToolCall("c1", "ask_admin", "{\"reason\":\"one\"}"),
                    new ToolCall("c2", "ask_admin", "{\"reason\":\"two\"}"),
                ]
            ),
            Replies.Answer(Kind, "Asked.")
        );

        await harness.Players.HandleAsync(new PlayerAsk().Game("help"), default);

        Assert.Single(harness.Services.GetRequiredService<HandoverStore>().Open);
        var conversation = harness.Conversations.List(
            ConversationStore.PlayerPrefix + "steam:76561198000000001"
        );
        var results = harness
            .Conversations.Get(
                ConversationStore.PlayerPrefix + "steam:76561198000000001",
                conversation[0].Id
            )!
            .Messages.Where(m => m.Role == ChatRole.Tool)
            .ToList();
        Assert.False(results[0].IsError);
        Assert.True(results[1].IsError);
        Assert.Contains("today's actions", results[1].Text);
    }

    [Fact]
    public async Task PrivacyNoticeComesFirstAndAgainAfterAMonth()
    {
        var time = new ManualTime();
        await using var harness = await StartAsync(time);
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "A"),
            Replies.Answer(Kind, "B"),
            Replies.Answer(Kind, "C")
        );
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(ask.Game("1"), default);
        time.Now = time.Now.AddDays(1);
        await harness.Players.HandleAsync(ask.Game("2"), default);
        time.Now = time.Now.AddDays(31);
        await harness.Players.HandleAsync(ask.Game("3"), default);

        Assert.Equal(5, ask.Replies.Count);
        // The default names the provider of the player role's backend.
        Assert.Contains("127.0.0.1", ask.Replies[0]);
        Assert.Equal(["A", "B"], ask.Replies[1..3]);
        Assert.StartsWith("Note:", ask.Replies[3]);
        Assert.Equal("C", ask.Replies[4]);

        harness.Configure(s => s with { Player = s.Player with { PrivacyNotice = "Custom." } });
        Assert.Equal("Custom.", PlayerRequestHandler.PrivacyNotice(harness.Settings.Current));
    }

    [Fact]
    public async Task QueueWaitCancelsAndTellsThePlayer()
    {
        await using var harness = await StartAsync();
        harness.ConfigurePlayer(role => role with { MaxConcurrentRuns = 1, QueueWaitSeconds = 1 });
        var release = new TaskCompletionSource();
        harness.Server.Enqueue(
            "p",
            async (request, response, token) =>
            {
                await release.Task.WaitAsync(token);
                await Replies.Answer(Kind, "first")(request, response, token);
            }
        );
        var first = new PlayerAsk();
        var second = new PlayerAsk();

        var running = harness.Players.HandleAsync(first.Game("first"), default);
        await Task.Delay(300);
        await harness.Players.HandleAsync(second.Game("second", "76561198000000002"), default);
        release.SetResult();
        await running;

        Assert.Contains("waited 1 s", second.Replies[^1]);
        Assert.Equal("first", first.Replies[^1]);
    }

    [Fact]
    public async Task UnlinkedDiscordUserGetsNoPlayerTools()
    {
        await using var harness = await StartAsync();
        harness.Server.Enqueue("p", Replies.Answer(Kind, "Hi"), Replies.Answer(Kind, "Hi"));
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(ask.Unlinked("hello"), default);
        await harness.Players.HandleAsync(ask.Game("hello"), default);

        var bodies = harness.Server.BodiesOf("p");
        var unlinked = ToolNames(bodies[0]);
        Assert.DoesNotContain(unlinked, name => name.StartsWith("my_", StringComparison.Ordinal));
        Assert.Contains("ask_admin", unlinked);
        Assert.Contains("docs_search", unlinked);
        Assert.Contains("not linked", bodies[0].GetRawText());
        var linked = ToolNames(bodies[1]);
        Assert.Contains("my_grids", linked);
        Assert.Contains("my_grid_restore", linked);
        // The admin's operations are never the player's.
        Assert.DoesNotContain(linked, name => name.Contains("servers_status"));
    }

    [Fact]
    public async Task AllBackendsFailingGivesAFixedWhisperAndAHandover()
    {
        await using var harness = await StartAsync();
        harness.Configure(s =>
            s with
            {
                Notifications = s.Notifications with { Discord = true },
                Discord = new() { AdminChannelId = "admins", PublicUrl = "https://q.example/" },
            }
        );
        harness.Server.Enqueue("p", Replies.Status(500));
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(ask.Game("Where is my ship?"), default);

        Assert.Equal(PlayerRequestHandler.Unavailable, ask.Replies[^1]);
        var handover = Assert.Single(harness.Services.GetRequiredService<HandoverStore>().Open);
        Assert.Contains("backends-failed", handover.Reason);
        Assert.Equal("steam:76561198000000001", handover.PlayerKey);
        Assert.Contains("Where is my ship?", handover.Excerpt);
        var (channel, text) = Assert.Single(harness.Discord.Posts);
        Assert.Equal("admins", channel);
        Assert.Contains("https://q.example/assistant/approvals", text);
    }

    [Fact]
    public async Task ConversationFollowsThePlayerAndStartsOverAfterAGap()
    {
        var time = new ManualTime();
        await using var harness = await StartAsync(time);
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "A"),
            Replies.Answer(Kind, "B"),
            Replies.Answer(Kind, "C")
        );
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(ask.Game("1"), default);
        // The same Steam ID from another cluster node continues the conversation.
        await harness.Players.HandleAsync(
            ask.Game("2") with
            {
                Source = new(
                    global::Quasar.Plugin.Abstractions.Assistant.QuasarRequestChannel.Game,
                    "alpha",
                    "node-2"
                ),
            },
            default
        );
        Assert.Equal(4, harness.Server.BodiesOf("p")[1].GetProperty("messages").GetArrayLength());

        time.Now = time.Now.Add(ConversationStore.SessionGap).AddMinutes(1);
        await harness.Players.HandleAsync(ask.Game("3"), default);
        Assert.Equal(2, harness.Server.BodiesOf("p")[2].GetProperty("messages").GetArrayLength());
    }
}
