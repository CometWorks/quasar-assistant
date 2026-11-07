using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>Creates proposals for change calls in <c>propose</c> mode, and approves, rejects and expires them.
/// Each outcome goes back to the conversation as a <c>[host]</c> line.</summary>
public sealed class ProposalService(
    IQuasarAuditLog audit,
    ProposalStore proposals,
    ConversationStore conversations,
    SettingsStore settings,
    AdminNotifier notifier,
    PlayerOutbox outbox,
    IEnumerable<QuasarOperation> operations,
    TimeProvider time
)
{
    private readonly Dictionary<string, QuasarOperation> _operations = operations.ToDictionary(op =>
        op.Key
    );

    public async Task<Proposal> CreateAsync(
        ClaimsPrincipal agent,
        QuasarOperation operation,
        JsonElement arguments,
        Conversation conversation,
        string? reason,
        CancellationToken token,
        IReadOnlyList<string>? details = null
    )
    {
        var entry = await audit.RecordProposalAsync(
            agent,
            operation.PluginId,
            operation.OperationId,
            arguments,
            reason,
            token
        );
        var proposal = new Proposal
        {
            Id = entry.Id,
            ConversationId = conversation.Id,
            AskerSubject = conversation.OwnerSubject,
            AskerName = conversation.OwnerName,
            Role = conversation.Role,
            PluginId = operation.PluginId,
            OperationId = operation.OperationId,
            Arguments = ResultFilter.Cap(ResultFilter.Json(arguments), 1000),
            Reason = reason,
            Details = details?.ToList(),
            Created = time.GetUtcNow(),
        };
        proposals.Add(proposal);
        var who = proposal.Role switch
        {
            AssistantRoles.Player =>
                $"The assistant proposes `{proposal.Operation}` for the player {proposal.AskerName}",
            AssistantRoles.Monitor => $"The monitor proposes `{proposal.Operation}`",
            _ => $"The assistant proposes `{proposal.Operation}` for {proposal.AskerName}",
        };
        await notifier.PostAsync($"{who}. Approve or reject it: {notifier.ApprovalsUrl}", token);
        return proposal;
    }

    /// <summary>Whether the person could approve or reject the operation, by its default policies. No person holds
    /// CanActAsPlayer, so CanControlServers stands in for it, as in core. Core decides on approval with the
    /// admins' rule overrides; this only hides buttons and notices that can't work.</summary>
    public async Task<bool> CanApproveAsync(
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        Proposal proposal
    )
    {
        if (!_operations.TryGetValue(proposal.Operation, out var operation))
            return false;
        foreach (var policy in operation.DefaultPolicies)
            if (
                !(
                    await authorization.AuthorizeAsync(
                        user,
                        null,
                        policy.Equals(
                            QuasarPolicyNames.CanActAsPlayer,
                            StringComparison.OrdinalIgnoreCase
                        )
                            ? QuasarPolicyNames.CanControlServers
                            : policy
                    )
                ).Succeeded
            )
                return false;
        return true;
    }

    /// <returns>Null on success, else why it didn't work.</returns>
    public async Task<string?> ApproveAsync(
        ClaimsPrincipal approver,
        string id,
        CancellationToken token
    )
    {
        if (!proposals.TryClaim(id))
            return "This proposal was already decided.";
        try
        {
            var result = await audit.ApproveAsync(approver, id, token);
            if (result.StatusCode is 401 or 403)
                return result.Error ?? "Quasar refused the approval."; // Still pending: another admin may approve.
            var ok = result.Error is null;
            var outcome = ok
                ? ResultFilter.Cap(ResultFilter.Json(result.Value), 2000)
                : result.Error!;
            Decide(
                id,
                approver,
                ok ? ProposalStatus.Approved : ProposalStatus.Failed,
                outcome,
                proposal =>
                    ok
                        ? $"Proposal {proposal.Id} ({proposal.Operation}) was approved by {Name(approver)} and ran. Result: {outcome}"
                        : $"Proposal {proposal.Id} ({proposal.Operation}) was approved by {Name(approver)} but failed: {outcome}"
            );
            return ok ? null : outcome;
        }
        finally
        {
            proposals.Release(id);
        }
    }

    public async Task<string?> RejectAsync(
        ClaimsPrincipal approver,
        string id,
        string? reason,
        CancellationToken token
    )
    {
        if (!proposals.TryClaim(id))
            return "This proposal was already decided.";
        try
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim();
            var result = await audit.RejectAsync(approver, id, reason, token);
            if (result.StatusCode is 401 or 403)
                return result.Error ?? "Quasar refused the rejection.";
            // 404 or 409: core has closed the proposal already, so the plugin closes its record too.
            Decide(
                id,
                approver,
                ProposalStatus.Rejected,
                reason,
                proposal =>
                    $"Proposal {proposal.Id} ({proposal.Operation}) was rejected by {Name(approver)}: {reason}"
            );
            return null;
        }
        finally
        {
            proposals.Release(id);
        }
    }

    /// <summary>Rejects the proposals pending longer than the expiry, with reason "expired".</summary>
    public async Task ExpireAsync(CancellationToken token)
    {
        var days = settings.Current.ProposalExpiryDays;
        var limit = time.GetUtcNow().AddDays(-days);
        foreach (var proposal in proposals.Pending.Where(p => p.Created < limit).ToList())
        {
            if (!proposals.TryClaim(proposal.Id))
                continue;
            try
            {
                // Core may have expired the entry itself; the plugin's record expires either way.
                await audit.RejectAsync(null, proposal.Id, "expired", token);
                Decide(
                    proposal.Id,
                    null,
                    ProposalStatus.Expired,
                    "expired",
                    p =>
                        $"Proposal {p.Id} ({p.Operation}) expired after {days} days without a decision and was rejected."
                );
            }
            finally
            {
                proposals.Release(proposal.Id);
            }
        }
        proposals.Forget(limit);
    }

    private void Decide(
        string id,
        ClaimsPrincipal? approver,
        ProposalStatus status,
        string outcome,
        Func<Proposal, string> note
    )
    {
        var decided = proposals.Decide(
            id,
            proposal =>
                proposal with
                {
                    Status = status,
                    DecidedBy = approver is null ? null : Name(approver),
                    Decided = time.GetUtcNow(),
                    Outcome = outcome,
                }
        );
        // A player hears it on their next request; nothing reaches them before.
        if (decided?.Role == AssistantRoles.Player)
            outbox.Add(
                ConversationStore.PlayerKey(decided.AskerSubject),
                PlayerNote(decided),
                time.GetUtcNow()
            );
        if (
            decided is not null
            && conversations.Get(decided.AskerSubject, decided.ConversationId) is { } conversation
        )
            conversations.Append(
                conversation,
                new ChatMessage
                {
                    Role = ChatRole.Host,
                    Text = note(decided),
                    ProposalId = decided.Id,
                }
            );
    }

    private static string Name(ClaimsPrincipal user) => user.Identity?.Name ?? "an admin";

    /// <summary>The decision in the player's words, without operation keys or proposal IDs.</summary>
    internal static string PlayerNote(Proposal proposal)
    {
        var what =
            proposal.Operation == ToolCatalog.RestoreKey
                ? "restore of "
                    + (
                        proposal.Details?.FirstOrDefault(line => line.StartsWith("Grid: "))?[6..]
                        ?? "a grid"
                    )
            : proposal.OperationId.StartsWith("mcp.", StringComparison.Ordinal) ? "an MCP tool call"
            : proposal.Operation;
        return proposal.Status switch
        {
            ProposalStatus.Approved =>
                $"An admin approved your request ({what}) and it was carried out.",
            ProposalStatus.Failed =>
                $"An admin approved your request ({what}), but it failed: {ResultFilter.Cap(proposal.Outcome ?? "", 300)}",
            ProposalStatus.Rejected =>
                $"An admin turned down your request ({what}): {proposal.Outcome}",
            _ => $"Your request ({what}) expired without an admin's decision.",
        };
    }
}
