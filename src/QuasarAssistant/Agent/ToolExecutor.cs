using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>The run a tool call belongs to.</summary>
/// <param name="Request">The asker's message, for the audit log.</param>
/// <param name="Reason">What the model said before the call, kept with a proposal.</param>
public sealed record ToolCallContext(
    Conversation Conversation,
    string Request,
    string? Reason,
    int CapBytes
)
{
    /// <summary>The person the admin assistant acts for.</summary>
    public ClaimsPrincipal? Asker { get; init; }

    /// <summary>The player agent core built for a player's request; null for a Discord user without a link.</summary>
    public ClaimsPrincipal? Agent { get; init; }

    /// <summary>Discord admin requests: every change becomes a proposal, whatever the mode.</summary>
    public bool ProposeOnly { get; init; }

    /// <summary>Counts an action against the player's daily limit; false when none is left. Null: no limit.</summary>
    public Func<bool>? TryUseAction { get; init; }

    /// <summary>Where the player asked, for a hand-over.</summary>
    public string? Source { get; init; }

    public string Role => Conversation.Role;
}

/// <summary>What a tool call gave the model, and the proposal it created if any.</summary>
public sealed record ToolOutcome(string Text, bool Error = false, string? ProposalId = null);

/// <summary>
/// Runs one tool call. Operations run as the agent principal through core's dispatcher (deny list, the agent's
/// role, the asker's policies, audit); a change in <c>propose</c> mode, any change asked on Discord and any acting
/// MCP tool only becomes a proposal. The model's arguments are data: they are passed on as JSON and never read as
/// instructions.
/// </summary>
public sealed class ToolExecutor(
    IQuasarAgentPrincipals principals,
    ProposalService proposals,
    GridRestores restores,
    HandoverService handovers,
    SkillLibrary skills,
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
        var outcome = await RunAsync(tools, call, run, token);
        return new ChatMessage
        {
            Role = ChatRole.Tool,
            ToolCallId = call.Id,
            ToolName = call.Name,
            Text = outcome.Text,
            IsError = outcome.Error,
            ProposalId = outcome.ProposalId,
        };
    }

    /// <summary>One dispatch through core, in a scope of its own.</summary>
    internal static async Task<QuasarOperationResult> DispatchAsync(
        IServiceScopeFactory scopes,
        ClaimsPrincipal agent,
        QuasarOperation operation,
        JsonElement arguments,
        CancellationToken token
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IQuasarOperationDispatcher>();
        var context = new QuasarOperationContext(agent, scope.ServiceProvider)
        {
            Kind = operation.Kind,
        };
        return await dispatcher.DispatchAsync(
            context,
            operation.PluginId,
            operation.OperationId,
            arguments,
            token
        );
    }

    private async Task<ToolOutcome> RunAsync(
        ToolSet tools,
        ToolCall call,
        ToolCallContext run,
        CancellationToken token
    )
    {
        // A tool that isn't offered, such as one in "off" mode, is never run whatever the model asks.
        if (!tools.ByName.TryGetValue(call.Name, out var tool))
            return new($"There is no tool named {call.Name}.", true);
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
            return new("The arguments are not valid JSON.", true);
        }

        switch (tool.Kind)
        {
            case ToolKind.SkillsList:
                return new(ResultFilter.Cap(skills.List(run.Role), run.CapBytes));
            case ToolKind.DocsSearch:
                return new(
                    ResultFilter.Cap(
                        skills.Search(run.Role, Text(arguments, "query") ?? ""),
                        run.CapBytes
                    )
                );
            case ToolKind.SkillRead:
                return new(
                    skills.Read(
                        run.Role,
                        Text(arguments, "name") ?? "",
                        arguments.ValueKind == JsonValueKind.Object
                        && arguments.TryGetProperty("offset", out var offset)
                        && offset.TryGetInt32(out var at)
                            ? at
                            : 0
                    )
                );
            case ToolKind.AskAdmin:
                if (run.TryUseAction?.Invoke() == false)
                    return new(ActionLimit, true);
                var handover = await handovers.CreateAsync(
                    run.Conversation,
                    run.Source,
                    Text(arguments, "reason") ?? "The player asked for an admin.",
                    token
                );
                return new(
                    $"Hand-over {handover.Id} created: the admins were asked and will answer the player themselves."
                );
        }

        ClaimsPrincipal agent;
        try
        {
            agent =
                run.Agent
                ?? await principals.CreateAssistantAsync(
                    run.Asker ?? throw new UnauthorizedAccessException("Nobody to act for."),
                    run.Conversation.Id,
                    run.Request
                );
        }
        catch (UnauthorizedAccessException error)
        {
            return new("Refused: " + error.Message, true);
        }

        try
        {
            if (tool.Kind == ToolKind.Restore)
                return await restores.RunAsync(tool, agent, arguments, run, token);

            var operation = tool.Operation!;
            List<string>? details = null;
            if (tool.Kind == ToolKind.Mcp)
            {
                arguments = JsonSerializer.SerializeToElement(
                    new McpCall(tool.Mcp!.Server, tool.Mcp.Name, arguments),
                    JsonSerializerOptions.Web
                );
                details = tool.IsChange
                    ?
                    [
                        $"MCP tool {tool.Mcp.Name} of the server {tool.Mcp.Server}.",
                        "Not reversible: what an MCP tool does can't be undone from Quasar.",
                    ]
                    : null;
            }

            if (tool.IsChange && run.TryUseAction?.Invoke() == false)
                return new(ActionLimit, true);
            if (tool.IsChange && (tool.Mode == ToolMode.Propose || run.ProposeOnly))
            {
                var proposal = await proposals.CreateAsync(
                    agent,
                    operation,
                    arguments,
                    run.Conversation,
                    run.Reason,
                    token,
                    details
                );
                return new(
                    $"Proposal {proposal.Id} created, waiting for an admin to approve or reject it. Nothing has changed yet.",
                    false,
                    proposal.Id
                );
            }

            var result = await DispatchAsync(scopes, agent, operation, arguments, token);
            if (result.Error is not null)
                return new(
                    ResultFilter.Cap(
                        $"Refused or failed (HTTP {result.StatusCode}): {result.Error}",
                        run.CapBytes
                    ),
                    true
                );
            return result.Value is QuasarRead read
                ? new(await ReadTextAsync(read, run.CapBytes, token))
                : new(ResultFilter.Cap(ResultFilter.Json(result.Value), run.CapBytes));
        }
        catch (Exception error)
            when (error is UnauthorizedAccessException or QuasarOperationException)
        {
            return new("Refused: " + error.Message, true);
        }
        catch (Exception error)
            when (error is JsonException or ArgumentException or FormatException)
        {
            return new("Invalid arguments: " + error.Message, true);
        }
    }

    private const string ActionLimit =
        "Refused: the player has used all of today's actions. Reads still work; tell the player to try again tomorrow.";

    internal static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
