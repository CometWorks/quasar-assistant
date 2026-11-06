using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Notifications;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.GridBackups;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Players;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;

namespace QuasarAssistant;

public static class AssistantServiceCollectionExtensions
{
    /// <summary>The plugin's ID, under which it registers its operations.</summary>
    public const string PluginId = "quasar.assistant";

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
        services.AddSingleton<PlayerOutbox>();
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
        services.AddSingleton<AskCommand>();
        services.AddSingleton<AssistantCommand>();
        services.AddSingleton<IQuasarChatCommand>(p => p.GetRequiredService<AskCommand>());
        services.AddSingleton<IQuasarChatCommand>(p => p.GetRequiredService<AssistantCommand>());
        services.AddHostedService<AssistantMaintenance>();
        AddMcpOperations(services);
        services.AddGridBackupsOperations();
        return services;
    }

    /// <summary>
    /// MCP calls go through core so they are checked and audited like any operation. Each role has its own pair:
    /// the admin assistant's under server control, the player agent's under <c>CanActAsPlayer</c>, which only the
    /// player agent meets. A handler accepts only its role's agent, for a server and tool set up for that role.
    /// An acting MCP tool can't be undone, so it is always a proposal.
    /// </summary>
    private static void AddMcpOperations(IServiceCollection services)
    {
        AddMcpOperations(
            services,
            AssistantRoles.Admin,
            QuasarPolicyNames.CanControlServers,
            "the assistant's settings"
        );
        AddMcpOperations(
            services,
            AssistantRoles.Player,
            QuasarPolicyNames.CanActAsPlayer,
            "the player support settings"
        );
    }

    private static void AddMcpOperations(
        IServiceCollection services,
        string role,
        string policy,
        string where
    )
    {
        var deadline = TimeSpan.FromSeconds(120);
        services.AddQuasarOperation<McpCall, JsonNode?>(
            McpRegistry.PluginId,
            McpRegistry.ReadOperation(role),
            [policy],
            (context, request, token) =>
                context
                    .Services.GetRequiredService<McpRegistry>()
                    .CallAsync(context.User, role, request, readOnly: true, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                Area = "mcp",
                Deadline = deadline,
                Description =
                    $"Calls a tool an admin marked read-only on a remote MCP server set up in {where}.",
            }
        );
        services.AddQuasarOperation<McpCall, JsonNode?>(
            McpRegistry.PluginId,
            McpRegistry.CallOperation(role),
            [policy],
            (context, request, token) =>
                context
                    .Services.GetRequiredService<McpRegistry>()
                    .CallAsync(context.User, role, request, readOnly: false, token),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Change,
                Area = "mcp",
                Reversal = QuasarReversal.NotReversible,
                Deadline = deadline,
                Description =
                    $"Calls a tool on a remote MCP server set up in {where}. What it does can't be undone from Quasar.",
            }
        );
    }
}

/// <summary>On start: fetches the MCP servers' tools. Hourly: deletes old conversations and expires old proposals
/// and hand-overs, and drops undelivered player notes as old.</summary>
internal sealed class AssistantMaintenance(
    SettingsStore settings,
    ConversationStore conversations,
    ProposalService proposals,
    HandoverStore handovers,
    PlayerOutbox outbox,
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
                var expired = time.GetUtcNow().AddDays(-settings.Current.ProposalExpiryDays);
                handovers.Forget(expired);
                outbox.Forget(expired);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Assistant cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
