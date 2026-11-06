using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Players;

/// <summary>
/// The admin command <c>/assistant</c> on Discord, answered only in the admin channel set in the settings. The
/// admin assistant runs for the asker with read and propose only: every change becomes a proposal, whatever the
/// mode, and approvals stay in the web UI (R5).
/// </summary>
public sealed class AssistantCommand(
    SettingsStore settings,
    ConversationStore conversations,
    AssistantRunner runner,
    AdminNotifier notifier,
    IServiceScopeFactory scopes,
    TimeProvider time
) : IQuasarChatCommand
{
    public QuasarChatCommandInfo Info { get; } =
        new(
            "assistant",
            "Ask the admin assistant (mapped Quasar admins only).",
            "question",
            "Your question",
            QuasarChatCommandScope.Admin
        );

    public const string WrongChannel =
        "The assistant answers only in the admin channel set in its settings.";
    public const string NotAllowed = "You are not allowed to use the assistant.";

    public async Task HandleAsync(QuasarChatCommandRequest request, CancellationToken token)
    {
        var channel = settings.Current.Discord.AdminChannelId.Trim();
        if (channel.Length == 0 || request.Source.DiscordChannelId != channel)
        {
            await request.ReplyAsync(WrongChannel, token);
            return;
        }
        // Core always names the mapped person for an admin command.
        if (request.Caller is not { } asker)
        {
            await request.ReplyAsync(NotAllowed, token);
            return;
        }
        await using (var scope = scopes.CreateAsyncScope())
        {
            var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
            if (
                !(
                    await authorization.AuthorizeAsync(
                        asker,
                        null,
                        QuasarPolicyNames.CanUseAssistant
                    )
                ).Succeeded
            )
            {
                await request.ReplyAsync(NotAllowed, token);
                return;
            }
        }

        var subject = asker.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var name = asker.Identity?.Name ?? subject;
        var conversation = conversations.Keyed(
            subject,
            name,
            AssistantRoles.Admin,
            "discord-admin:" + subject
        );
        var ask = new AskRequest(
            request.Text,
            [
                Prompt.Time(time.GetUtcNow()),
                $"asker: {name} ({subject})",
                "asked: on Discord, in the admin channel. Changes can only be proposed here; they are approved on the Approvals page.",
            ]
        )
        {
            Asker = asker,
            ProposeOnly = true,
        };
        var before = conversation.Messages.Count;
        var result = await runner.AskAsync(conversation, ask, token);
        var text = result is null
            ? "The assistant is still working on your last question."
            : result.Text.Trim();
        if (
            result is not null
            && conversation.Messages.Skip(before).Any(m => m.ProposalId is not null)
        )
            text += $"\nApprovals: {notifier.ApprovalsUrl}";
        // Core sends the reply without resolving mentions.
        await request.ReplyAsync(text.Length == 0 ? "(No answer.)" : text, token);
    }
}
