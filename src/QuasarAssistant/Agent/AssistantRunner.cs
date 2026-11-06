using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Agent;

/// <summary>What to answer and as whom.</summary>
/// <param name="HostLines">The <c>[host]</c> context lines of the user turn.</param>
public sealed record AskRequest(string Text, IReadOnlyList<string> HostLines)
{
    /// <summary>The person the admin assistant acts for.</summary>
    public ClaimsPrincipal? Asker { get; init; }

    /// <summary>The player agent core built; null for a Discord user without a link.</summary>
    public ClaimsPrincipal? Agent { get; init; }

    /// <summary>Every change becomes a proposal, whatever the mode (Discord admin requests).</summary>
    public bool ProposeOnly { get; init; }

    public Func<bool>? TryUseAction { get; init; }
    public string? Source { get; init; }

    /// <summary>Uncached input and output tokens the run may use; it stops before the next model turn once they
    /// are used up. Null: no budget.</summary>
    public long? TokenBudget { get; init; }
}

/// <param name="Outcome"><c>ok</c>, <c>busy</c>, <c>deadline</c>, <c>turn-limit</c>, <c>budget</c>,
/// <c>backends-failed</c>, <c>rejected</c>, <c>stopped</c> or <c>error</c>, as in the metrics.</param>
/// <param name="Text">The final answer, or what stopped the run.</param>
public sealed record RunResult(string Outcome, string Text)
{
    public TokenUsage Usage { get; init; } = TokenUsage.None;
    public IReadOnlyList<string> Tools { get; init; } = [];
}

/// <summary>
/// Runs the agent loop: the prompt prefix, the conversation and the new user turn go to the role's backends; the
/// tool calls in the answer run one after another and their results go back, until the model answers without a
/// tool call. Runs share a pool per role (at most N at once, a queue wait) and each has a deadline and a turn limit.
/// </summary>
public sealed class AssistantRunner(
    SettingsStore settings,
    ToolCatalog catalog,
    BackendRouter router,
    ToolExecutor tools,
    ConversationStore conversations,
    MetricsLog metrics,
    TimeProvider time,
    ILogger<AssistantRunner> logger
)
{
    private readonly ConcurrentDictionary<string, (int Size, SemaphoreSlim Slots)> _pools = new();
    private readonly ConcurrentDictionary<string, Run> _running = new();

    public bool IsRunning(string conversationId) => _running.ContainsKey(conversationId);

    /// <summary>The answer streaming in, or null.</summary>
    public string? Draft(string conversationId) =>
        _running.TryGetValue(conversationId, out var run) ? run.Draft : null;

    /// <summary>The run's state for the chat, such as "Waiting for a free slot".</summary>
    public string? Status(string conversationId) =>
        _running.TryGetValue(conversationId, out var run) ? run.Status : null;

    public void Stop(string conversationId)
    {
        if (_running.TryGetValue(conversationId, out var run))
            run.Stop.Cancel();
    }

    /// <summary>Adds a person's message from the web chat and answers it. Returns false when the conversation
    /// already runs.</summary>
    public async Task<bool> AskAsync(
        Conversation conversation,
        ClaimsPrincipal asker,
        string text,
        PageContext page
    )
    {
        var subject = asker.FindFirstValue(ClaimTypes.NameIdentifier) ?? conversation.OwnerSubject;
        var request = new AskRequest(
            text,
            Prompt.HostLines(time.GetUtcNow(), asker.Identity?.Name ?? subject, subject, page)
        )
        {
            Asker = asker,
        };
        return await AskAsync(conversation, request, CancellationToken.None) is not null;
    }

    /// <summary>Adds the message and answers it in the conversation's role. Null when the conversation already
    /// runs.</summary>
    public async Task<RunResult?> AskAsync(
        Conversation conversation,
        AskRequest request,
        CancellationToken token
    )
    {
        var run = new Run();
        if (!_running.TryAdd(conversation.Id, run))
            return null;
        var cancel = token.Register(run.Stop.Cancel);
        try
        {
            conversations.Append(
                conversation,
                new ChatMessage
                {
                    Role = ChatRole.User,
                    Text = request.Text,
                    HostLines = request.HostLines,
                }
            );
            return await RunAsync(conversation, request, run);
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "Assistant run failed for conversation {Conversation}",
                conversation.Id
            );
            var text = "The run failed: " + error.Message;
            conversations.Append(conversation, Error(text));
            return new RunResult("error", text);
        }
        finally
        {
            _running.TryRemove(conversation.Id, out _);
            cancel.Dispose();
            run.Stop.Dispose();
            conversations.Touch(conversation.Id);
        }
    }

    private async Task<RunResult> RunAsync(Conversation conversation, AskRequest ask, Run run)
    {
        var current = settings.Current;
        var role = current.Role(conversation.Role);
        var backends = current.BackendsOf(conversation.Role);
        var clock = Stopwatch.StartNew();
        var record = new RunMetrics { Time = time.GetUtcNow(), Role = conversation.Role };
        List<BackendAttempt> attempts = [];
        List<string> answeredBy = [];
        List<string> called = [];
        var usage = TokenUsage.None;
        var turns = 0;
        var queueSeconds = 0.0;
        string outcome;
        string reply = "";

        var pool = Pool(conversation.Role, role.MaxConcurrentRuns);
        run.Status = "Waiting for a free slot";
        conversations.Touch(conversation.Id);
        bool entered;
        try
        {
            entered = await pool.WaitAsync(
                TimeSpan.FromSeconds(role.QueueWaitSeconds),
                run.Stop.Token
            );
        }
        catch (OperationCanceledException)
        {
            conversations.Append(conversation, Error("Stopped."));
            return new RunResult("stopped", "Stopped.");
        }
        if (!entered)
        {
            reply =
                $"The assistant is busy: your request waited {role.QueueWaitSeconds} s for a free slot and was canceled. Try again later.";
            conversations.Append(conversation, Error(reply));
            metrics.Append(
                record with
                {
                    Outcome = "busy",
                    QueueSeconds = clock.Elapsed.TotalSeconds,
                    Seconds = clock.Elapsed.TotalSeconds,
                }
            );
            return new RunResult("busy", reply);
        }
        queueSeconds = clock.Elapsed.TotalSeconds;
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(role.RunDeadlineSeconds),
            time
        );
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            run.Stop.Token,
            deadline.Token
        );
        try
        {
            var canAct = conversation.Role != AssistantRoles.Player || ask.Agent is not null;
            var toolSet = await catalog.ForAsync(role, conversation.Role, canAct);
            var system = Prompt.System(conversation.Role, role);
            outcome = "turn-limit";
            while (turns < role.MaxTurns)
            {
                if (ask.TokenBudget is { } budget && usage.Uncached >= budget)
                {
                    outcome = "budget";
                    reply = "Stopped: the token budget is used up.";
                    conversations.Append(conversation, Error(reply));
                    break;
                }
                turns++;
                run.Status = "Thinking";
                conversations.Touch(conversation.Id);
                var request = new ChatRequest(system, toolSet.Definitions, conversation.Messages);
                var routed = await router.CompleteAsync(
                    backends,
                    settings.ApiKey,
                    request,
                    draft =>
                    {
                        run.Draft = draft;
                        conversations.Touch(conversation.Id);
                    },
                    linked.Token
                );
                run.Draft = null;
                attempts.AddRange(routed.Attempts);
                answeredBy.Add(routed.Backend);
                var answer = routed.Response;
                usage = usage.Add(answer.Usage);
                var text = AnswerText(answer);
                conversations.Append(
                    conversation,
                    new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Text = text,
                        ToolCalls = answer.ToolCalls,
                        Thinking = answer.Thinking.Count > 0 ? answer.Thinking : null,
                    }
                );
                if (answer.ToolCalls.Count == 0)
                {
                    outcome = "ok";
                    reply = text;
                    break;
                }
                var context = new ToolCallContext(
                    conversation,
                    ask.Text,
                    Reason(answer.Text),
                    current.ResultCapBytes
                )
                {
                    Asker = ask.Asker,
                    Agent = ask.Agent,
                    ProposeOnly = ask.ProposeOnly,
                    TryUseAction = ask.TryUseAction,
                    Source = ask.Source,
                };
                foreach (var call in answer.ToolCalls)
                {
                    run.Status = "Running " + call.Name;
                    conversations.Touch(conversation.Id);
                    called.Add(call.Name);
                    conversations.Append(
                        conversation,
                        await tools.ExecuteAsync(toolSet, call, context, linked.Token)
                    );
                }
            }
            if (outcome == "turn-limit")
            {
                reply =
                    $"Stopped after {role.MaxTurns} model turns without a final answer. Ask again to go on.";
                conversations.Append(conversation, Error(reply));
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            outcome = "deadline";
            reply =
                $"Stopped: the run passed its {role.RunDeadlineSeconds} s deadline. Ask again to go on.";
            conversations.Append(conversation, Error(reply));
        }
        catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
        {
            outcome = "stopped";
            reply = "Stopped.";
            conversations.Append(conversation, Error(reply));
        }
        catch (AllBackendsFailed failure)
        {
            outcome = "backends-failed";
            attempts.AddRange(failure.Attempts);
            reply = Describe(failure, backends.Count);
            conversations.Append(conversation, Error(reply));
        }
        catch (BackendRejected rejected)
        {
            outcome = "rejected";
            reply = "The backend rejected the request: " + rejected.Message;
            conversations.Append(conversation, Error(reply));
        }
        finally
        {
            pool.Release();
            run.Draft = null;
            CloseOpenCalls(conversation);
        }
        metrics.Append(
            record with
            {
                Outcome = outcome,
                Backends = answeredBy,
                Fallovers = attempts.Count(a => a.Outcome != "ok"),
                Failures = attempts.Where(a => a.Outcome != "ok").ToList(),
                Turns = turns,
                Tools = called,
                PromptTokens = usage.Prompt,
                CachedTokens = usage.Cached,
                CompletionTokens = usage.Completion,
                QueueSeconds = queueSeconds,
                Seconds = clock.Elapsed.TotalSeconds,
            }
        );
        return new RunResult(outcome, reply) { Usage = usage, Tools = called };
    }

    private SemaphoreSlim Pool(string role, int size)
    {
        size = Math.Max(1, size);
        // A changed size gets a new pool; runs on the old one finish there.
        return _pools
            .AddOrUpdate(
                role,
                _ => (size, new SemaphoreSlim(size)),
                (_, pool) => pool.Size == size ? pool : (size, new SemaphoreSlim(size))
            )
            .Slots;
    }

    // Every tool call needs a result before the next request, or the APIs refuse the conversation.
    private void CloseOpenCalls(Conversation conversation)
    {
        var messages = conversation.Messages;
        var last = messages.FindLastIndex(m => m.Role == ChatRole.Assistant);
        if (last < 0 || messages[last].ToolCalls is not { Count: > 0 } calls)
            return;
        var answered = messages
            .Skip(last + 1)
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.ToolCallId)
            .ToHashSet();
        foreach (var call in calls.Where(call => !answered.Contains(call.Id)))
            conversations.Append(
                conversation,
                new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Text = "Not run: the run stopped first.",
                    IsError = true,
                }
            );
    }

    private static string AnswerText(ChatResponse answer) =>
        answer.Stop switch
        {
            StopKind.Refusal when answer.Text.Length == 0 => "(The model declined to answer.)",
            StopKind.Length when answer.ToolCalls.Count == 0 => answer.Text
                + "\n\n(The answer was cut off at the backend's token limit.)",
            _ => answer.Text,
        };

    private static string? Reason(string text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : text.Length > 500 ? text[..500] + "…"
        : text.Trim();

    private static string Describe(AllBackendsFailed failure, int configured)
    {
        if (configured == 0)
            return "No backend is set up for the assistant. An admin can add one in the assistant's settings.";
        var text = new StringBuilder("Every backend failed, so there is no answer. Backends:");
        foreach (var state in failure.States)
        {
            text.Append($"\n- {state.Backend}: ");
            text.Append(
                state.CooldownUntil is { } until
                    ? $"cooling down until {until.UtcDateTime:HH:mm:ss} UTC"
                    : "available"
            );
            if (state.FailuresInARow > 0)
                text.Append($", {state.FailuresInARow} failures in a row");
            if (state.LastError is { } error)
                text.Append($", last error: {error}");
        }
        return text.ToString();
    }

    private static ChatMessage Error(string text) => new() { Role = ChatRole.Error, Text = text };

    private sealed class Run
    {
        public CancellationTokenSource Stop { get; } = new();
        public volatile string? Draft;
        public volatile string? Status;
    }
}
