using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Discord;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Agent;

/// <summary>Posts new proposals and hand-overs to the admin Discord channel, when that channel is enabled.</summary>
public sealed class AdminNotifier(
    SettingsStore settings,
    IServiceProvider services,
    ILogger<AdminNotifier> logger
)
{
    /// <summary>The Approvals page, absolute when Quasar's public URL is set.</summary>
    public string ApprovalsUrl =>
        settings.Current.Discord.PublicUrl.Trim().TrimEnd('/') + "/assistant/approvals";

    public async Task PostAsync(string text, CancellationToken token)
    {
        var current = settings.Current;
        if (
            !current.Notifications.Discord
            || string.IsNullOrWhiteSpace(current.Discord.AdminChannelId)
        )
            return;
        // Core's Discord bot; missing when Quasar runs without the Discord integration.
        if (services.GetService<IQuasarDiscordChannels>() is not { } discord)
            return;
        try
        {
            if (!await discord.PostAsync(current.Discord.AdminChannelId.Trim(), text, token))
                logger.LogWarning("The assistant could not post to the admin Discord channel");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "The assistant could not post to the admin Discord channel");
        }
    }
}
