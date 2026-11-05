using Microsoft.Extensions.DependencyInjection;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

/// <summary>Players hear about admins' decisions on their proposals on their next request.</summary>
public sealed class OutboxTests
{
    private const BackendKind Kind = BackendKind.OpenAi;
    private const string SteamId = "76561198000000001";

    /// <summary>A player's restore request that becomes a proposal (the restore tool defaults to propose).</summary>
    private static async Task<(Harness Harness, ManualTime Time, string ProposalId)> ProposeAsync()
    {
        var time = new ManualTime();
        var harness = await Harness.StartAsync(PlayerTests.Operations(), time);
        harness.UsePlayerBackends(Kind, "p");
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "", [new ToolCall("c1", "my_grid_restore", "{\"gridId\":42}")]),
            Replies.Answer(Kind, "An admin will decide.")
        );
        await harness.Players.HandleAsync(
            new PlayerAsk().Game("Restore my Miner", SteamId),
            default
        );
        var proposal = Assert.Single(harness.Services.GetRequiredService<ProposalStore>().Pending);
        return (harness, time, proposal.Id);
    }

    [Fact]
    public async Task ApprovalIsToldFirstOnTheNextRequestEvenAfterTheConversationStartsOver()
    {
        var (harness, time, id) = await ProposeAsync();
        await using var _ = harness;
        var proposals = harness.Services.GetRequiredService<ProposalService>();
        Assert.Null(await proposals.ApproveAsync(Harness.Person("Admin", "steam:9"), id, default));

        time.Now = time.Now.Add(ConversationStore.SessionGap).AddHours(1);
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "Hello again."),
            Replies.Answer(Kind, "Hi.")
        );
        var ask = new PlayerAsk();
        await harness.Players.HandleAsync(ask.Game("Is it back?", SteamId), default);

        Assert.Equal(2, ask.Replies.Count);
        Assert.StartsWith("An admin approved your request (restore of", ask.Replies[0]);
        Assert.DoesNotContain(id, ask.Replies[0]);
        Assert.Equal("Hello again.", ask.Replies[1]);
        // The note went before the model: the model's request doesn't carry it.
        Assert.DoesNotContain(
            "approved your request",
            harness.Server.BodiesOf("p")[^1].GetRawText()
        );

        // Told once.
        await harness.Players.HandleAsync(ask.Game("Thanks", SteamId), default);
        Assert.Equal("Hi.", ask.Replies[^1]);
        Assert.Equal(3, ask.Replies.Count);
    }

    [Fact]
    public async Task RejectionAndExpiryAreToldToo()
    {
        var (harness, time, id) = await ProposeAsync();
        await using var _ = harness;
        var proposals = harness.Services.GetRequiredService<ProposalService>();
        await proposals.RejectAsync(
            Harness.Person("Admin", "steam:9"),
            id,
            "It was a raid loss.",
            default
        );

        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "", [new ToolCall("c2", "my_grid_restore", "{\"gridId\":43}")]),
            Replies.Answer(Kind, "Asked again."),
            Replies.Answer(Kind, "Sorry.")
        );
        var ask = new PlayerAsk();
        await harness.Players.HandleAsync(ask.Game("Why not?", SteamId), default);
        Assert.StartsWith("An admin turned down your request", ask.Replies[0]);
        Assert.EndsWith(": It was a raid loss.", ask.Replies[0]);

        time.Now = time.Now.AddDays(31);
        await proposals.ExpireAsync(default);
        await harness.Players.HandleAsync(ask.Game("Any news?", SteamId), default);
        Assert.Contains(
            ask.Replies,
            reply => reply.Contains("expired without an admin's decision")
        );
    }

    [Fact]
    public async Task OldNotesExpireWithTheProposals()
    {
        await using var harness = await Harness.StartAsync([]);
        var outbox = harness.Services.GetRequiredService<PlayerOutbox>();
        var now = DateTimeOffset.UtcNow;
        outbox.Add("steam:1", "old", now.AddDays(-40));
        outbox.Add("steam:1", "new", now);
        outbox.Add("steam:2", "old", now.AddDays(-40));

        outbox.Forget(now.AddDays(-30));

        Assert.Equal(["new"], outbox.Take("steam:1"));
        Assert.Empty(outbox.Take("steam:1"));
        Assert.Empty(outbox.Take("steam:2"));
    }
}
