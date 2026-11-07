using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>The run a tool call belongs to.</summary>
/// <param name="Request">The asker's message, for the audit log.</param>
/// <param name="Reason">What the model said before the call, kept with a proposal.</param>
public sealed record ToolCallContext(
    ClaimsPrincipal Asker,
    Conversation Conversation,
    string Request,
    string? Reason,
    int CapBytes
);

/// <summary>
/// Runs one tool call as the assistant principal, through core's dispatcher (deny list, the agent's role, the
/// asker's policies, audit). A change in <c>propose</c> mode only becomes a proposal. The model's arguments are
/// data: they are passed on as JSON and never read as instructions.
/// </summary>
public sealed class ToolExecutor(
    IQuasarAgentPrincipals principals,
    ProposalService proposals,
    IServiceScopeFactory scopes
)
{
    public async Task<ChatMessage> ExecuteAsync(
        ToolSet tools,
        ToolCall call,
        ToolCallContext run,
        CancellationToken token
    )
    {
        var (text, error, proposalId) = await RunAsync(tools, call, run, token);
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            ToolCallId = call.Id,
            ToolName = call.Name,
            Text = text,
            IsError = error,
            ProposalId = proposalId,
        };
    }

    private async Task<(string Text, bool Error, string? ProposalId)> RunAsync(
        ToolSet tools,
        ToolCall call,
        ToolCallContext run,
        CancellationToken token
    )
    {
        // A tool that isn't offered, such as one in "off" mode, is never dispatched whatever the model asks.
        if (!tools.ByName.TryGetValue(call.Name, out var tool))
            return ($"There is no tool named {call.Name}.", true, null);
        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments,
                new JsonDocumentOptions { MaxDepth = QuasarSdkLimits.MaximumJsonDepth }
            );
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ("The arguments are not valid JSON.", true, null);
        }

        ClaimsPrincipal agent;
        try
        {
            agent = await principals.CreateAssistantAsync(
                run.Asker,
                run.Conversation.Id,
                run.Request
            );
        }
        catch (UnauthorizedAccessException error)
        {
            return ("Refused: " + error.Message, true, null);
        }

        var operation = tool.Operation;
        try
        {
            if (tool.IsChange && tool.Mode == ToolMode.Propose)
            {
                var proposal = await proposals.CreateAsync(
                    agent,
                    operation,
                    arguments,
                    run.Conversation,
                    run.Reason,
                    token
                );
                return (
                    $"Proposal {proposal.Id} created, waiting for an admin to approve or reject it. Nothing has changed yet.",
                    false,
                    proposal.Id
                );
            }

            await using var scope = scopes.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IQuasarOperationDispatcher>();
            var context = new QuasarOperationContext(agent, scope.ServiceProvider)
            {
                Kind = operation.Kind,
            };
            var result = await dispatcher.DispatchAsync(
                context,
                operation.PluginId,
                operation.OperationId,
                arguments,
                token
            );
            if (result.Error is not null)
                return (
                    ResultFilter.Cap(
                        $"Refused or failed (HTTP {result.StatusCode}): {result.Error}",
                        run.CapBytes
                    ),
                    true,
                    null
                );
            return result.Value is QuasarRead read
                ? (await ReadTextAsync(read, run.CapBytes, token), false, null)
                : (ResultFilter.Cap(ResultFilter.Json(result.Value), run.CapBytes), false, null);
        }
        catch (Exception error)
            when (error is UnauthorizedAccessException or QuasarOperationException)
        {
            return ("Refused: " + error.Message, true, null);
        }
        catch (Exception error)
            when (error is JsonException or ArgumentException or FormatException)
        {
            return ("Invalid arguments: " + error.Message, true, null);
        }
    }

    private static async Task<string> ReadTextAsync(
        QuasarRead read,
        int capBytes,
        CancellationToken token
    )
    {
        if (!IsText(read.ContentType))
            return $"Binary content ({read.ContentType}, {read.Length?.ToString() ?? "unknown"} bytes), not shown.";
        await using var stream = await read.OpenAsync(token);
        // One byte more than the cap tells whether there is more.
        var buffer = new byte[capBytes + 1];
        var count = 0;
        while (
            count < buffer.Length
            && await stream.ReadAsync(buffer.AsMemory(count), token) is var n and > 0
        )
            count += n;
        var text = ResultFilter.RedactText(Encoding.UTF8.GetString(buffer, 0, count));
        return count <= capBytes
            ? text
            : ResultFilter.Cut(
                text,
                capBytes,
                read.Length is { } length ? $"{length} bytes" : "longer"
            );
    }

    private static bool IsText(string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);
}
