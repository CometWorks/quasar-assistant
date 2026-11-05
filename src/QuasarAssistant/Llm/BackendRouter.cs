using System.Collections.Concurrent;

namespace QuasarAssistant.Llm;

/// <summary>What happened to one backend during one model turn.</summary>
public sealed record BackendAttempt(string Backend, string Outcome, string? Error = null);

/// <summary>A backend's health as the chat shows it when every backend failed.</summary>
public sealed record BackendStatus(
    string Backend,
    DateTimeOffset? CooldownUntil,
    int FailuresInARow,
    string? LastError
);

public sealed class AllBackendsFailed(
    IReadOnlyList<BackendAttempt> attempts,
    IReadOnlyList<BackendStatus> states
) : Exception("Every backend of the role failed.")
{
    public IReadOnlyList<BackendAttempt> Attempts { get; } = attempts;
    public IReadOnlyList<BackendStatus> States { get; } = states;
}

public sealed record RoutedResponse(
    ChatResponse Response,
    string Backend,
    IReadOnlyList<BackendAttempt> Attempts
);

/// <summary>
/// Clangy's failover: backends in the role's order, skipping those in cooldown. A failure cools a backend down for
/// its cooldown, doubled per failure in a row up to the maximum, or for its Retry-After if longer; a success resets
/// the count. A turn cut off by max_tokens before any answer goes to the next backend without a cooldown.
/// </summary>
public sealed class BackendRouter(LlmClient client, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, BackendState> _states = new();

    public async Task<RoutedResponse> CompleteAsync(
        IReadOnlyList<BackendSettings> backends,
        Func<string, string?> apiKey,
        ChatRequest request,
        Action<string>? onText,
        CancellationToken token
    )
    {
        List<BackendAttempt> attempts = [];
        ChatResponse? cutOff = null;
        string? cutOffBackend = null;
        var estimate = EstimateTokens(request);
        foreach (var backend in backends)
        {
            var state = State(backend);
            var now = time.GetUtcNow();
            if (!state.Available(now))
            {
                attempts.Add(new(backend.Name, "cooling down", state.LastError));
                continue;
            }
            if (estimate + backend.MaxTokens > backend.ContextWindow)
            {
                attempts.Add(
                    new(
                        backend.Name,
                        "skipped",
                        $"the conversation (~{estimate} tokens) needs a larger context window"
                    )
                );
                continue;
            }
            await state.Slots.WaitAsync(token);
            ChatResponse response;
            try
            {
                onText?.Invoke("");
                response = await client.SendAsync(
                    backend,
                    apiKey(backend.Name),
                    request,
                    onText,
                    token
                );
            }
            catch (BackendFailure failure)
            {
                state.Failed(time.GetUtcNow(), failure.Message, failure.RetryAfter);
                attempts.Add(new(backend.Name, "failed", failure.Message));
                continue;
            }
            finally
            {
                state.Slots.Release();
            }
            if (
                response.Stop == StopKind.Length
                && (response.Text.Length == 0 || response.ToolCalls.Count > 0)
            )
            {
                // Typically a thinking model that is still thinking; its partial tool calls can't run either.
                attempts.Add(new(backend.Name, "cut off", "max_tokens reached before an answer"));
                cutOff ??= response;
                cutOffBackend ??= backend.Name;
                continue;
            }
            state.Succeeded();
            attempts.Add(new(backend.Name, "ok"));
            return new(response, backend.Name, attempts);
        }
        if (cutOff is not null && cutOff.Text.Length > 0)
            return new(cutOff with { ToolCalls = [] }, cutOffBackend!, attempts);
        throw new AllBackendsFailed(
            attempts,
            backends
                .Select(backend => State(backend).Status(backend.Name, time.GetUtcNow()))
                .ToList()
        );
    }

    public IReadOnlyList<BackendStatus> Statuses(IEnumerable<BackendSettings> backends) =>
        backends.Select(backend => State(backend).Status(backend.Name, time.GetUtcNow())).ToList();

    // ponytail: four characters a token; good enough to skip a backend whose window is far too small.
    internal static int EstimateTokens(ChatRequest request) =>
        (
            request.System.Length
            + request.Tools.Sum(tool =>
                tool.Name.Length + tool.Description.Length + tool.Schema.GetRawText().Length
            )
            + request.Messages.Sum(message =>
                message.ModelText.Length
                + (message.ToolCalls?.Sum(call => call.Arguments.Length) ?? 0)
            )
        ) / 4;

    private BackendState State(BackendSettings backend)
    {
        var state = _states.GetOrAdd(backend.Name, _ => new BackendState(backend));
        if (state.Settings != backend)
        {
            // Settings changed: keep the health, take the new limits.
            var updated = new BackendState(backend, state);
            _states[backend.Name] = updated;
            return updated;
        }
        return state;
    }

    private sealed class BackendState
    {
        private readonly object _lock = new();
        private DateTimeOffset _cooldownUntil;

        public BackendState(BackendSettings settings, BackendState? previous = null)
        {
            Settings = settings;
            Slots = new SemaphoreSlim(Math.Max(1, settings.Parallel));
            if (previous is null)
                return;
            _cooldownUntil = previous._cooldownUntil;
            Failures = previous.Failures;
            LastError = previous.LastError;
        }

        public BackendSettings Settings { get; }
        public SemaphoreSlim Slots { get; }
        public int Failures { get; private set; }
        public string? LastError { get; private set; }

        public bool Available(DateTimeOffset now)
        {
            lock (_lock)
                return now >= _cooldownUntil;
        }

        public void Failed(DateTimeOffset now, string error, TimeSpan? retryAfter)
        {
            lock (_lock)
            {
                // Requests that were already running when it failed don't count again.
                if (now >= _cooldownUntil)
                    Failures++;
                LastError = error;
                var cooldown = Math.Min(
                    Settings.Cooldown * Math.Pow(2, Failures - 1),
                    Settings.MaxCooldown
                );
                var wait = TimeSpan.FromSeconds(Math.Max(cooldown, retryAfter?.TotalSeconds ?? 0));
                _cooldownUntil = now + wait;
            }
        }

        public void Succeeded()
        {
            lock (_lock)
                Failures = 0;
        }

        public BackendStatus Status(string name, DateTimeOffset now)
        {
            lock (_lock)
                return new(name, _cooldownUntil > now ? _cooldownUntil : null, Failures, LastError);
        }
    }
}
