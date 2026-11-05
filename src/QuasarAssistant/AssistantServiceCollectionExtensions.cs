using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Players;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;

namespace QuasarAssistant;

public static class AssistantServiceCollectionExtensions
{
    /// <param name="dataDirectory">The plugin's folder for settings, keys, conversations, proposals and metrics.</param>
    public static IServiceCollection AddQuasarAssistant(
        this IServiceCollection services,
        string dataDirectory
    )
    {
        services.AddSingleton(new AssistantPaths(dataDirectory));
        services.TryAddSingleton(TimeProvider.System);
        // Timeouts are the backends' own first-byte and stall limits.
        services.AddSingleton(new LlmClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        // The operation deadline cuts a call short before this.
        services.AddSingleton(
            new McpClient(
                new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(150),
                    MaxResponseContentBufferSize = 4 * 1024 * 1024,
                }
            )
        );
        services.AddSingleton<BackendRouter>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<ProposalStore>();
        services.AddSingleton<HandoverStore>();
        services.AddSingleton<PlayerUsageStore>();
        services.AddSingleton<MetricsLog>();
        services.AddSingleton<SkillLibrary>();
        services.AddSingleton<McpRegistry>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<AdminNotifier>();
        services.AddSingleton<ProposalService>();
        services.AddSingleton<HandoverService>();
        services.AddSingleton<GridRestores>();
        services.AddSingleton<ToolExecutor>();
        services.AddSingleton<AssistantRunner>();
        services.AddSingleton<IQuasarNoticeSource, AssistantNotices>();
        services.AddSingleton<IQuasarPlayerRequestHandler, PlayerRequestHandler>();
        services.AddSingleton<IQuasarAdminRequestHandler, DiscordAdminHandler>();
        services.AddHostedService<AssistantMaintenance>();
        AddMcpOperations(services);
        return services;
    }

    /// <summary>
    /// MCP calls go through core so they are checked and audited like any operation. The handlers only accept
    /// the assistant's agents, for a server and tool set up for the agent's role. An acting MCP tool can't be
    /// undone, so it is always a proposal.
    /// </summary>
    private static void AddMcpOperations(IServiceCollection services)
    {
        string[] policies = [QuasarPolicyNames.CanControlServers];
        var deadline = TimeSpan.FromSeconds(120);
        services.AddQuasarOperation<McpCall, JsonNode?>(
            McpRegistry.PluginId,
            McpRegistry.ReadOperation,
            policies,
            (context, request, token) =>
                context
                    .Services.GetRequiredService<McpRegistry>()
                    .CallAsync(context.User, request, readOnly: true, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Area = "mcp",
                Deadline = deadline,
                Description =
                    "Calls a tool an admin marked read-only on a remote MCP server set up in the assistant's settings.",
            }
        );
        services.AddQuasarOperation<McpCall, JsonNode?>(
            McpRegistry.PluginId,
            McpRegistry.CallOperation,
            policies,
            (context, request, token) =>
                context
                    .Services.GetRequiredService<McpRegistry>()
                    .CallAsync(context.User, request, readOnly: false, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Area = "mcp",
                Reversal = QuasarReversal.NotReversible,
                Deadline = deadline,
                Description =
                    "Calls a tool on a remote MCP server set up in the assistant's settings. What it does can't be undone from Quasar.",
            }
        );
    }
}

/// <summary>On start: fetches the MCP servers' tools. Hourly: deletes old conversations and expires old proposals
/// and hand-overs.</summary>
internal sealed class AssistantMaintenance(
    SettingsStore settings,
    ConversationStore conversations,
    ProposalService proposals,
    HandoverStore handovers,
    McpRegistry mcp,
    TimeProvider time,
    ILogger<AssistantMaintenance> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await mcp.RefreshAsync(token);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                conversations.Expire(settings.Current.ConversationRetentionDays);
                await proposals.ExpireAsync(token);
                handovers.Forget(time.GetUtcNow().AddDays(-settings.Current.ProposalExpiryDays));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Assistant cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
