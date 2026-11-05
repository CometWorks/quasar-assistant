using System.Security.Claims;
using Quasar.Plugin.Abstractions.Operations;

namespace QuasarAssistant.Agent;

// The Quasar contracts this plugin needs beyond QSR-4 PR 1 come from PR 2 (operation descriptions and request
// types, the proposal API) and PR 3 (web push notices). Until those land, the plugin reaches them only through
// this file, so switching over is one place.

internal static class OperationInfo
{
    /// <summary>The operation's own description for the model. PR 2: <c>Metadata.Description</c>.</summary>
    public static string? Description(QuasarOperation operation) => null;

    /// <summary>The request type, for the tool's JSON schema. PR 2: <c>QuasarOperation.RequestType</c>; until then
    /// the generic argument of the registration helpers' operation class.</summary>
    public static Type? RequestType(QuasarOperation operation)
    {
        for (var type = operation.GetType(); type is not null; type = type.BaseType)
            if (type.IsGenericType && type.DeclaringType == typeof(QuasarOperation))
                return type.GetGenericArguments()[0];
        return null;
    }
}

/// <summary>Approves or rejects a proposal recorded in the audit log. Core rebuilds the agent principal from the
/// proposal's entry and checks the approver, the asker and the deny list at that moment.</summary>
public interface IProposalDecisions
{
    Task<QuasarOperationResult> ApproveAsync(
        ClaimsPrincipal approver,
        string entryId,
        CancellationToken token
    );

    /// <param name="approver">Null when the proposal expired.</param>
    Task RejectAsync(
        ClaimsPrincipal? approver,
        string entryId,
        string reason,
        CancellationToken token
    );
}

/// <summary>Until Quasar has the proposal API (QSR-4 PR 2): approving fails with a clear message, rejecting is
/// recorded by the plugin only.</summary>
internal sealed class MissingCoreProposalDecisions : IProposalDecisions
{
    public Task<QuasarOperationResult> ApproveAsync(
        ClaimsPrincipal approver,
        string entryId,
        CancellationToken token
    ) =>
        Task.FromResult(
            new QuasarOperationResult(
                501,
                Error: "This Quasar version can't approve proposals yet."
            )
        );

    public Task RejectAsync(
        ClaimsPrincipal? approver,
        string entryId,
        string reason,
        CancellationToken token
    ) => Task.CompletedTask;
}
