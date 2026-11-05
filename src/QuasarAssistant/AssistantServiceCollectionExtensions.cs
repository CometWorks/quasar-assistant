using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
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
        services.AddSingleton<BackendRouter>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<ProposalStore>();
        services.AddSingleton<MetricsLog>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ToolExecutor>();
        services.AddSingleton<ProposalService>();
        services.AddSingleton<AssistantRunner>();
        services.TryAddSingleton<IProposalDecisions, MissingCoreProposalDecisions>();
        services.AddHostedService<AssistantMaintenance>();
        return services;
    }
}

/// <summary>Hourly: deletes old conversations and expires old proposals.</summary>
internal sealed class AssistantMaintenance(
    SettingsStore settings,
    ConversationStore conversations,
    ProposalService proposals,
    ILogger<AssistantMaintenance> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                conversations.Expire(settings.Current.ConversationRetentionDays);
                await proposals.ExpireAsync(token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Assistant cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
