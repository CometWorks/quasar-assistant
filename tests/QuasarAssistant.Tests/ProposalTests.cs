using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

public sealed class ProposalTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    private static async Task<(Harness Harness, Conversation Conversation)> ProposeAsync(
        TimeProvider? time = null
    )
    {
        var harness = await Harness.StartAsync([TestOperation.Change("servers.start")], time);
        harness.UseBackends(Kind, "main");
        harness.Server.Enqueue(
            "main",
            Replies.Answer(
                Kind,
                "",
                [new ToolCall("c1", "quasar_core__servers_start", "{\"server\":\"alpha\"}")]
            ),
            Replies.Answer(Kind, "Proposed."),
            Replies.Answer(Kind, "It ran.")
        );
        return (harness, await harness.AskAsync("Start alpha"));
    }

    [Fact]
    public async Task ApprovalGoesBackToTheConversationAndTheModel()
    {
        var (harness, conversation) = await ProposeAsync();
        await using var _ = harness;
        var service = harness.Services.GetRequiredService<ProposalService>();

        Assert.Null(
            await service.ApproveAsync(
                Harness.Person("Grace", "steam:9"),
                "entry1",
                CancellationToken.None
            )
        );

        var proposal = harness.Services.GetRequiredService<ProposalStore>().Get("entry1")!;
        Assert.Equal(ProposalStatus.Approved, proposal.Status);
        Assert.Equal("Grace", proposal.DecidedBy);
        var note = conversation.Messages[^1];
        Assert.Equal(ChatRole.Host, note.Role);
        Assert.Contains("approved by Grace", note.Text);

        // The next turn tells the model.
        await harness.Runner.AskAsync(
            conversation,
            Harness.Person(),
            "Did it work?",
            new PageContext(null, null)
        );
        var messages = harness.Server.BodiesOf("main")[2].GetProperty("messages");
        var user = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.StartsWith(
            "[host] Proposal entry1 (quasar.core/servers.start) was approved by Grace",
            user
        );
    }

    [Fact]
    public async Task ARefusedApprovalLeavesTheProposalPending()
    {
        var (harness, _) = await ProposeAsync();
        await using var __ = harness;
        harness.Audit.ApproveResult = new QuasarOperationResult(
            403,
            Error: "The approver cannot access this SDK operation."
        );
        var service = harness.Services.GetRequiredService<ProposalService>();

        Assert.Contains(
            "approver cannot",
            await service.ApproveAsync(
                Harness.Person("Eve", "steam:5"),
                "entry1",
                CancellationToken.None
            )
        );
        Assert.Single(harness.Services.GetRequiredService<ProposalStore>().Pending);
    }

    [Fact]
    public async Task RejectionIsDecidedOnce()
    {
        var (harness, conversation) = await ProposeAsync();
        await using var _ = harness;
        var service = harness.Services.GetRequiredService<ProposalService>();

        Assert.Null(
            await service.RejectAsync(
                Harness.Person("Grace", "steam:9"),
                "entry1",
                "not now",
                CancellationToken.None
            )
        );
        Assert.NotNull(
            await service.ApproveAsync(
                Harness.Person("Grace", "steam:9"),
                "entry1",
                CancellationToken.None
            )
        );
        Assert.Contains("rejected by Grace: not now", conversation.Messages[^1].Text);
        Assert.Equal(("entry1", "not now", false), Assert.Single(harness.Audit.Rejected));
    }

    [Fact]
    public async Task OldProposalsExpire()
    {
        var time = new ManualTime();
        var (harness, conversation) = await ProposeAsync(time);
        await using var _ = harness;
        time.Now = time.Now.AddDays(31);

        await harness
            .Services.GetRequiredService<ProposalService>()
            .ExpireAsync(CancellationToken.None);

        Assert.Equal(
            ProposalStatus.Expired,
            harness.Services.GetRequiredService<ProposalStore>().Get("entry1")!.Status
        );
        Assert.Equal(("entry1", "expired", true), Assert.Single(harness.Audit.Rejected));
        Assert.Contains("expired after 30 days", conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task WebPushGoesToThoseWhoCouldApprove()
    {
        var (harness, _) = await ProposeAsync();
        await using var _ = harness;
        var notices = harness.Services.GetRequiredService<IQuasarNoticeSource>();

        var admin = await notices.GetNoticesAsync(
            Harness.Person("Grace", "steam:9", "admin"),
            default
        );
        Assert.Equal("quasar.assistant/proposal/entry1", Assert.Single(admin).Key);
        Assert.Equal("/assistant/approvals", admin[0].Url);
        Assert.Empty(
            await notices.GetNoticesAsync(Harness.Person("Vic", "steam:8", "guest"), default)
        );

        harness.Configure(settings => settings with { Notifications = new() { WebPush = false } });
        Assert.Empty(
            await notices.GetNoticesAsync(Harness.Person("Grace", "steam:9", "admin"), default)
        );
    }

    [Fact]
    public async Task PlayerProposalsNeedServerControlToApprove()
    {
        await using var harness = await Harness.StartAsync([]);
        var service = harness.Services.GetRequiredService<ProposalService>();
        var authorization = harness.Services.GetRequiredService<IAuthorizationService>();
        var player = new Proposal
        {
            Id = "p",
            ConversationId = "c",
            AskerSubject = "steam:1",
            PluginId = "quasar.core",
            OperationId = "my.grid.restore",
        };
        // Unknown to this harness: nobody sees the buttons.
        Assert.False(await service.CanApproveAsync(Harness.Person(), authorization, player));

        await using var withOperation = await Harness.StartAsync([
            new TestOperation("my.grid.restore", policies: [QuasarPolicyNames.CanActAsPlayer]),
        ]);
        service = withOperation.Services.GetRequiredService<ProposalService>();
        authorization = withOperation.Services.GetRequiredService<IAuthorizationService>();
        Assert.True(
            await service.CanApproveAsync(Harness.Person(role: "editor"), authorization, player)
        );
        Assert.False(
            await service.CanApproveAsync(Harness.Person(role: "viewer"), authorization, player)
        );
    }
}
