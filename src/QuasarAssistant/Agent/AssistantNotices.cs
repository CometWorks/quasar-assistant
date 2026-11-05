using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>Web push: a notice per pending proposal to each admin who could approve it, and per open hand-over to
/// each admin who controls servers.</summary>
public sealed class AssistantNotices(
    SettingsStore settings,
    ProposalStore proposals,
    HandoverStore handovers,
    ProposalService service,
    IServiceScopeFactory scopes
) : IQuasarNoticeSource
{
    public async ValueTask<IReadOnlyList<QuasarNotice>> GetNoticesAsync(
        ClaimsPrincipal subscriber,
        CancellationToken token
    )
    {
        if (!settings.Current.Notifications.WebPush)
            return [];
        await using var scope = scopes.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        List<QuasarNotice> notices = [];
        foreach (var proposal in proposals.Pending)
            if (await service.CanApproveAsync(subscriber, authorization, proposal))
                notices.Add(
                    new QuasarNotice(
                        // Core sends each key once per browser, so a proposal is announced once.
                        "quasar.assistant/proposal/" + proposal.Id,
                        "The assistant proposes a change",
                        $"{proposal.AskerName} asked; the assistant proposes {proposal.Operation}. Approve or reject it.",
                        "/assistant/approvals",
                        proposal.Created
                    )
                );
        if (
            handovers.Open.Any()
            && (
                await authorization.AuthorizeAsync(
                    subscriber,
                    null,
                    QuasarPolicyNames.CanControlServers
                )
            ).Succeeded
        )
            foreach (var handover in handovers.Open)
                notices.Add(
                    new QuasarNotice(
                        "quasar.assistant/handover/" + handover.Id,
                        "A player needs an admin",
                        $"{handover.PlayerName}: {handover.Reason}",
                        "/assistant/approvals",
                        handover.Created,
                        "warning"
                    )
                );
        return notices;
    }
}
