using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Commands;
using QuasarAssistant.Llm;
using QuasarAssistant.Players;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

public sealed class DiscordAdminTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    public static AssistantCommand Handler(Harness harness) =>
        harness.Services.GetRequiredService<AssistantCommand>();

    public static (QuasarChatCommandRequest Request, List<string> Replies) Ask(
        string text,
        string channel = "admins"
    )
    {
        List<string> replies = [];
        return (
            new QuasarChatCommandRequest(
                "assistant",
                Harness.Person(),
                "discord:7",
                text,
                new QuasarChatCommandSource(
                    QuasarChatChannel.Discord,
                    DiscordUserId: "7",
                    DiscordChannelId: channel
                ),
                (reply, _) =>
                {
                    replies.Add(reply);
                    return Task.CompletedTask;
                }
            ),
            replies
        );
    }

    private static async Task<Harness> StartAsync()
    {
        var harness = await Harness.StartAsync([
            TestOperation.Read("servers.status"),
            TestOperation.Change("servers.start"),
        ]);
        harness.UseBackends(Kind, "main");
        harness.Configure(s =>
            s with
            {
                Discord = new() { AdminChannelId = "admins", PublicUrl = "https://q.example" },
                Notifications = s.Notifications with { Discord = true },
            }
        );
        return harness;
    }

    [Fact]
    public async Task OtherChannelsAreRefused()
    {
        await using var harness = await StartAsync();
        var (request, replies) = Ask("Start alpha", channel: "general");

        await Handler(harness).HandleAsync(request, default);

        Assert.Equal([AssistantCommand.WrongChannel], replies);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task ChangesAreAlwaysProposalsOnDiscord()
    {
        await using var harness = await StartAsync();
        harness.ConfigureRole(role =>
            role with
            {
                AreaModes = new() { ["servers"] = ToolMode.Apply },
            }
        );
        harness.Server.Enqueue(
            "main",
            Replies.Answer(
                Kind,
                "",
                [
                    new ToolCall("c1", "quasar_core__servers_status", "{}"),
                    new ToolCall("c2", "quasar_core__servers_start", "{\"server\":\"alpha\"}"),
                ]
            ),
            Replies.Answer(Kind, "Proposed a start, @everyone.")
        );
        var (request, replies) = Ask("Start alpha");

        await Handler(harness).HandleAsync(request, default);

        Assert.Equal(["quasar.core/servers.status"], harness.Dispatched.Keys);
        Assert.Equal("quasar.core/servers.start", Assert.Single(harness.Audit.Proposals).Key);
        var reply = Assert.Single(replies);
        // Core sends it without resolving mentions, so the text goes as the model wrote it.
        Assert.StartsWith("Proposed a start, @everyone.", reply, StringComparison.Ordinal);
        Assert.Contains("https://q.example/assistant/approvals", reply);
        // The proposal is announced in the admin channel too.
        Assert.Contains(harness.Discord.Posts, post => post.Text.Contains("servers.start"));
        // The conversation is the asker's, with the admin role.
        var conversation = harness.Conversations.List("steam:1");
        Assert.Single(conversation);
    }
}
