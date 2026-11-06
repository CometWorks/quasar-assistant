using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Assistant;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Monitoring;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>Web push: a notice per pending proposal to each admin who could approve it, and per open hand-over
/// and monitor run to each admin who controls servers.</summary>
public sealed class AssistantNotices(
    SettingsStore settings,
    ProposalStore proposals,
    HandoverStore handovers,
    IncidentStore incidents,
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
                        proposal.Role == AssistantRoles.Monitor
                            ? $"The monitor proposes {proposal.Operation}. Approve or reject it."
                            : $"{proposal.AskerName} asked; the assistant proposes {proposal.Operation}. Approve or reject it.",
                        "/assistant/approvals",
                        proposal.Created
                    )
                );
        var controls = (
            await authorization.AuthorizeAsync(
                subscriber,
                null,
                QuasarPolicyNames.CanControlServers
            )
        ).Succeeded;
        if (controls)
            foreach (var incident in incidents.All.Where(i => i.Runs.Count > 0))
            {
                var run = incident.Runs[^1];
                notices.Add(
                    new QuasarNotice(
                        // One notice per run: a recheck is announced again.
                        $"quasar.assistant/incident/{incident.Id}/{incident.Runs.Count}",
                        "The monitor found a problem",
                        $"{incident.Text} {Cut(run.Answer, 300)}",
                        "/assistant/monitor",
                        run.Time,
                        "warning"
                    )
                );
            }
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

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";
}
