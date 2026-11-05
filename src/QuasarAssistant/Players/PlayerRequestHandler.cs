using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Players;

/// <summary>
/// Answers players: <c>!ask</c> in game and <c>/ask</c> on Discord. In order, and no model runs before the last
/// step: off → a fixed reply; the decisions on their proposals since the last request; over a limit → a fixed
/// reply; the privacy notice on the first request and monthly;
/// then the player agent runs in the player pool and its answer is whispered or posted.
/// </summary>
public sealed class PlayerRequestHandler(
    SettingsStore settings,
    PlayerUsageStore usage,
    PlayerOutbox outbox,
    ConversationStore conversations,
    AssistantRunner runner,
    HandoverService handovers,
    TimeProvider time
) : IQuasarPlayerRequestHandler
{
    public const string NotEnabled = "The assistant isn't enabled on this server.";
    public const string Unavailable = "The assistant is unavailable right now. An admin was told.";
    public const string StillWorking =
        "The assistant is still working on your last question. Wait for its answer.";

    public async Task HandleAsync(QuasarPlayerRequest request, CancellationToken token)
    {
        var current = settings.Current;
        var player = current.Player;
        if (!player.Enabled)
        {
            await request.ReplyAsync(NotEnabled, token);
            return;
        }
        // Decisions on the player's proposals since their last request, before anything else.
        foreach (var note in outbox.Take(request.ConversationKey))
            await request.ReplyAsync(note, token);
        if (usage.TryRequest(request.ConversationKey, player.Limits) is { } limit)
        {
            await request.ReplyAsync(limit, token);
            return;
        }
        if (usage.TakeNoticeDue(request.ConversationKey))
            await request.ReplyAsync(PrivacyNotice(current), token);

        QuasarAgentIdentity? agent = null;
        request.Player?.IsQuasarAgent(out agent);
        var name = agent?.PlayerName ?? "Discord user " + request.Source.DiscordUserId;
        var source = Source(request.Source);
        var conversation = conversations.Keyed(
            ConversationStore.PlayerPrefix + request.ConversationKey,
            name,
            AssistantRoles.Player,
            request.ConversationKey
        );
        var ask = new AskRequest(request.Text, HostLines(agent, request, source))
        {
            Agent = request.Player,
            TryUseAction = () => usage.TryAction(request.ConversationKey, player.Limits),
            Source = source,
        };
        var result = await runner.AskAsync(conversation, ask, token);
        if (result is null)
        {
            await request.ReplyAsync(StillWorking, token);
            return;
        }
        if (result.Outcome is "backends-failed" or "rejected" or "error")
        {
            // R12: the player hears a fixed line; the admins get the error as a hand-over.
            await handovers.CreateAsync(
                conversation,
                source,
                $"The assistant could not answer ({result.Outcome}): {result.Text}",
                token
            );
            await request.ReplyAsync(Unavailable, token);
            return;
        }
        await request.ReplyAsync(
            string.IsNullOrWhiteSpace(result.Text) ? "(No answer.)" : result.Text.Trim(),
            token
        );
    }

    /// <summary>The admin's text, or a default naming the providers of the player role's backends.</summary>
    public static string PrivacyNotice(AssistantSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Player.PrivacyNotice))
            return settings.Player.PrivacyNotice.Trim();
        var providers = settings
            .BackendsOf(AssistantRoles.Player)
            .Select(backend =>
                Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out var url)
                    ? url.Host
                    : backend.Name
            )
            .Distinct()
            .ToList();
        var who =
            providers.Count == 0
                ? "an external AI provider"
                : "an AI provider (" + string.Join(", ", providers) + ")";
        return $"Note: what you ask the assistant is sent to {who} to answer it. Don't share passwords or personal details.";
    }

    private IReadOnlyList<string> HostLines(
        QuasarAgentIdentity? agent,
        QuasarPlayerRequest request,
        string source
    ) =>
        [
            Prompt.Time(time.GetUtcNow()),
            agent is not null
                ? $"player: {agent.PlayerName} (Steam ID {agent.PlayerSteamId})"
                : $"player: Discord user {request.Source.DiscordUserId}, not linked to a Steam account",
            "asked: " + source,
        ];

    private static string Source(QuasarPlayerRequestSource source) =>
        source.Channel == QuasarRequestChannel.Discord ? "on Discord"
        : source.Node is { } node ? $"in game on {source.Server}, node {node}"
        : $"in game on {source.Server}";
}
