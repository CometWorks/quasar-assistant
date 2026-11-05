using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

/// <summary>The player role's MCP servers, through its own CanActAsPlayer operations.</summary>
public sealed class PlayerMcpTests
{
    private const BackendKind Kind = BackendKind.OpenAi;
    private const string SteamId = "76561198000000001";

    private static async Task<(Harness Harness, StubMcpServer Mcp)> StartAsync()
    {
        var mcp = await StubMcpServer.StartAsync();
        var harness = await Harness.StartAsync(PlayerTests.Operations());
        harness.UsePlayerBackends(Kind, "p");
        harness.ConfigurePlayer(role =>
            role with
            {
                McpServers =
                [
                    new McpServerSettings
                    {
                        Name = "ops",
                        Url = mcp.Url,
                        ReadOnlyTools = ["lookup"],
                    },
                ],
            }
        );
        await harness.Services.GetRequiredService<McpRegistry>().RefreshAsync();
        return (harness, mcp);
    }

    private static List<ChatMessage> ToolResults(Harness harness)
    {
        var owner = ConversationStore.PlayerPrefix + "steam:" + SteamId;
        var id = harness.Conversations.List(owner)[0].Id;
        return harness
            .Conversations.Get(owner, id)!
            .Messages.Where(m => m.Role == ChatRole.Tool)
            .ToList();
    }

    [Fact]
    public async Task PlayerReadToolRunsThroughThePlayerOperation()
    {
        var (harness, mcp) = await StartAsync();
        await using var _m = mcp;
        await using var _h = harness;
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "", [new ToolCall("c1", "mcp_ops_lookup", "{\"id\":\"7\"}")]),
            Replies.Answer(Kind, "Ticket 7 is open.")
        );

        await harness.Players.HandleAsync(
            new PlayerAsk().Game("Is my ticket open?", SteamId),
            default
        );

        var (key, _, user) = Assert.Single(harness.Dispatched.Calls);
        Assert.Equal("quasar.assistant/mcp.player.read", key);
        Assert.True(user.IsQuasarAgent(out var agent));
        Assert.Equal(QuasarAgentKind.Player, agent.Kind);
        Assert.Single(mcp.Calls);
        Assert.False(Assert.Single(ToolResults(harness)).IsError);
    }

    [Fact]
    public async Task PlayerActingToolIsAProposalAndCountsAsAnAction()
    {
        var (harness, mcp) = await StartAsync();
        await using var _m = mcp;
        await using var _h = harness;
        harness.Configure(s =>
            s with
            {
                Player = s.Player with { Limits = new() { ActionsPerDay = 1 } },
            }
        );
        harness.ConfigurePlayer(role =>
            role with
            {
                AreaModes = new() { ["mcp"] = ToolMode.Apply },
            }
        );
        harness.Server.Enqueue(
            "p",
            Replies.Answer(
                Kind,
                "",
                [
                    new ToolCall("c1", "mcp_ops_restart", "{}"),
                    new ToolCall("c2", "mcp_ops_restart", "{}"),
                ]
            ),
            Replies.Answer(Kind, "Asked an admin.")
        );

        await harness.Players.HandleAsync(new PlayerAsk().Game("Restart it", SteamId), default);

        Assert.Empty(mcp.Calls);
        Assert.Empty(harness.Dispatched.Calls);
        Assert.Equal(
            "quasar.assistant/mcp.player.call",
            Assert.Single(harness.Audit.Proposals).Key
        );
        var results = ToolResults(harness);
        Assert.NotNull(results[0].ProposalId);
        Assert.True(results[1].IsError);
        Assert.Contains("today's actions", results[1].Text);
        // R15: server control stands in for CanActAsPlayer when deciding.
        var proposal = harness
            .Services.GetRequiredService<ProposalStore>()
            .Get(results[0].ProposalId!)!;
        Assert.Equal(AssistantRoles.Player, proposal.Role);
        Assert.True(
            await harness
                .Services.GetRequiredService<ProposalService>()
                .CanApproveAsync(
                    Harness.Person(role: "editor"),
                    harness.Services.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>(),
                    proposal
                )
        );
    }

    [Fact]
    public async Task EachRoleOnlyUsesItsOwnOperations()
    {
        var (harness, mcp) = await StartAsync();
        await using var _m = mcp;
        await using var _h = harness;
        harness.ConfigureRole(role =>
            role with
            {
                McpServers =
                [
                    new McpServerSettings
                    {
                        Name = "ops",
                        Url = mcp.Url,
                        ReadOnlyTools = ["lookup"],
                    },
                ],
            }
        );
        await harness.Services.GetRequiredService<McpRegistry>().RefreshAsync();
        var scopes = harness.Services.GetRequiredService<IServiceScopeFactory>();
        var operations = harness
            .Services.GetServices<QuasarOperation>()
            .ToDictionary(op => op.OperationId);
        var admin = await harness
            .Services.GetRequiredService<IQuasarAgentPrincipals>()
            .CreateAssistantAsync(Harness.Person(), "c", "r");
        var player = PlayerAsk.Agent(SteamId);
        var request = JsonSerializer.SerializeToElement(
            new McpCall("ops", "lookup"),
            JsonSerializerOptions.Web
        );

        async Task<int> Dispatch(System.Security.Claims.ClaimsPrincipal user, string operation) =>
            (
                await ToolExecutor.DispatchAsync(
                    scopes,
                    user,
                    operations[operation],
                    request,
                    default
                )
            ).StatusCode;

        // The policies keep them apart in core...
        Assert.Equal(200, await Dispatch(player, "mcp.player.read"));
        Assert.Equal(200, await Dispatch(admin, "mcp.read"));
        Assert.Equal(403, await Dispatch(admin, "mcp.player.read"));
        Assert.Equal(403, await Dispatch(player, "mcp.read"));

        // ...and the handlers do too, whatever an RBAC override says.
        async Task<int> Invoke(System.Security.Claims.ClaimsPrincipal user, string operation)
        {
            try
            {
                await operations[operation]
                    .InvokeAsync(
                        new QuasarOperationContext(user, harness.Services),
                        request,
                        default
                    );
                return 200;
            }
            catch (QuasarOperationException error)
            {
                return error.StatusCode;
            }
        }
        Assert.Equal(403, await Invoke(admin, "mcp.player.read"));
        Assert.Equal(403, await Invoke(admin, "mcp.player.call"));
        Assert.Equal(403, await Invoke(player, "mcp.read"));
        Assert.Equal(403, await Invoke(player, "mcp.call"));
        Assert.Equal(QuasarReversal.NotReversible, operations["mcp.player.call"].Metadata.Reversal);
        Assert.Equal(
            [QuasarPolicyNames.CanActAsPlayer],
            operations["mcp.player.call"].DefaultPolicies
        );

        // The admin's tools come from its own operations.
        var tools = await harness
            .Services.GetRequiredService<ToolCatalog>()
            .ForAsync(harness.Settings.Current.Role(AssistantRoles.Admin));
        Assert.Equal("mcp.read", tools.ByName["mcp_ops_lookup"].Operation!.OperationId);
    }
}
