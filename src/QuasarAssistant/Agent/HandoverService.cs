using System.Text;
using QuasarAssistant.Llm;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>Hands a player's conversation to the admins: the Approvals page, web push and the Discord channel.</summary>
public sealed class HandoverService(HandoverStore store, AdminNotifier notifier, TimeProvider time)
{
    private const int ExcerptMessages = 12;
    private const int ExcerptMessageChars = 400;

    public async Task<Handover> CreateAsync(
        Conversation conversation,
        string? source,
        string reason,
        CancellationToken token
    )
    {
        var handover = new Handover
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Created = time.GetUtcNow(),
            ConversationId = conversation.Id,
            PlayerKey = conversation.OwnerSubject.StartsWith(
                ConversationStore.PlayerPrefix,
                StringComparison.Ordinal
            )
                ? conversation.OwnerSubject[ConversationStore.PlayerPrefix.Length..]
                : conversation.OwnerSubject,
            PlayerName = conversation.OwnerName,
            Source = source ?? "",
            Reason = Shorten(reason.Trim(), 1000),
            Excerpt = Excerpt(conversation),
        };
        store.Add(handover);
        await notifier.PostAsync(
            $"A player needs an admin: {handover.PlayerName} ({handover.PlayerKey}), {handover.Source}. "
                + $"Reason: {handover.Reason}\nSee the Approvals page: {notifier.ApprovalsUrl}",
            token
        );
        return handover;
    }

    private static string Excerpt(Conversation conversation)
    {
        var text = new StringBuilder();
        foreach (
            var message in conversation
                .Messages.Where(m =>
                    m.Role is ChatRole.User or ChatRole.Assistant && m.Text.Length > 0
                )
                .TakeLast(ExcerptMessages)
        )
            text.Append(message.Role == ChatRole.User ? "Player: " : "Assistant: ")
                .Append(Shorten(message.Text.Trim(), ExcerptMessageChars))
                .Append('\n');
        return text.ToString();
    }

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";
}
