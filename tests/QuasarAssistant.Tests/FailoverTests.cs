using QuasarAssistant.Llm;

namespace QuasarAssistant.Tests;

public sealed class FailoverTests : IAsyncLifetime
{
    private StubLlmServer _server = null!;
    private readonly ManualTime _time = new();
    private BackendRouter _router = null!;

    public async Task InitializeAsync()
    {
        _server = await StubLlmServer.StartAsync();
        _router = new BackendRouter(
            new LlmClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
            _time
        );
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private BackendSettings Backend(string name, BackendKind kind = BackendKind.OpenAi) =>
        new()
        {
            Name = name,
            Kind = kind,
            BaseUrl = _server.BaseUrl(name),
            Model = name,
            FirstByteTimeout = 1,
            StallTimeout = 1,
            Cooldown = 60,
            MaxCooldown = 200,
        };

    private static readonly ChatRequest Request = new(
        "system",
        [],
        [new ChatMessage { Role = ChatRole.User, Text = "hi" }]
    );

    private Task<RoutedResponse> CompleteAsync(params BackendSettings[] backends) =>
        _router.CompleteAsync(backends, _ => "key", Request, null, CancellationToken.None);

    public static TheoryData<string, BackendKind> Failures() =>
        new()
        {
            { "429", BackendKind.OpenAi },
            { "500", BackendKind.OpenAi },
            { "401", BackendKind.Anthropic },
            { "stream-error", BackendKind.OpenAi },
            { "stream-error", BackendKind.Anthropic },
            { "first-byte", BackendKind.OpenAi },
            { "stall", BackendKind.Anthropic },
            { "max-tokens", BackendKind.OpenAi },
            { "max-tokens", BackendKind.Anthropic },
        };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FallsOverToTheNextBackend(string failure, BackendKind kind)
    {
        _server.Enqueue(
            "a",
            failure switch
            {
                "stream-error" => Replies.ErrorInStream(kind),
                "first-byte" => Replies.Silent(),
                "stall" => Replies.Stalls(kind),
                // Thinking until the limit: no answer text.
                "max-tokens" => Replies.Answer(
                    kind,
                    "",
                    stop: kind == BackendKind.Anthropic ? "max_tokens" : "length"
                ),
                _ => Replies.Status(int.Parse(failure)),
            }
        );
        _server.Enqueue("b", Replies.Answer(kind, "from b"));

        var result = await CompleteAsync(Backend("a", kind), Backend("b", kind));

        Assert.Equal("b", result.Backend);
        Assert.Equal("from b", result.Response.Text);
        Assert.Equal(failure == "max-tokens" ? "cut off" : "failed", result.Attempts[0].Outcome);
        // The next backend got the same request.
        Assert.Equal(
            _server.BodiesOf("a")[0].GetProperty("messages").GetRawText(),
            _server.BodiesOf("b")[0].GetProperty("messages").GetRawText()
        );
    }

    [Fact]
    public async Task FallsOverWhenTheConnectionIsRefused()
    {
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "up"));
        var result = await CompleteAsync(
            Backend("down") with
            {
                BaseUrl = StubLlmServer.ClosedUrl(),
            },
            Backend("b")
        );
        Assert.Equal("b", result.Backend);
        Assert.StartsWith("connection failed", result.Attempts[0].Error);
    }

    [Fact]
    public async Task CutOffDoesNotCoolTheBackendDown()
    {
        _server.Enqueue(
            "a",
            Replies.Answer(BackendKind.OpenAi, "", stop: "length"),
            Replies.Answer(BackendKind.OpenAi, "back")
        );
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b"));
        await CompleteAsync(Backend("a"), Backend("b"));
        Assert.Equal("a", (await CompleteAsync(Backend("a"), Backend("b"))).Backend);
    }

    [Fact]
    public async Task CutOffAnswerWithTextIsKeptWhenNoBackendIsLeft()
    {
        _server.Enqueue("a", Replies.Answer(BackendKind.OpenAi, "half an answ", stop: "length"));
        var result = await CompleteAsync(Backend("a"));
        Assert.Equal(StopKind.Length, result.Response.Stop);
        Assert.Equal("half an answ", result.Response.Text);
    }

    [Fact]
    public async Task CooldownDoublesPerFailureAndResetsOnSuccess()
    {
        var a = Backend("a");
        var b = Backend("b");
        _server.Enqueue("a", Replies.Status(500));
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b1"));
        await CompleteAsync(a, b);
        Assert.Equal(_time.Now.AddSeconds(60), _router.Statuses([a])[0].CooldownUntil);

        // While cooling down a isn't asked.
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b2"));
        Assert.Equal("cooling down", (await CompleteAsync(a, b)).Attempts[0].Outcome);
        Assert.Single(_server.BodiesOf("a"));

        // Second failure in a row: 120 s; third: capped at the maximum, 200 s.
        _time.Now = _time.Now.AddSeconds(61);
        _server.Enqueue("a", Replies.Status(503));
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b3"));
        await CompleteAsync(a, b);
        Assert.Equal(_time.Now.AddSeconds(120), _router.Statuses([a])[0].CooldownUntil);
        _time.Now = _time.Now.AddSeconds(121);
        _server.Enqueue("a", Replies.Status(503));
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b4"));
        await CompleteAsync(a, b);
        Assert.Equal(_time.Now.AddSeconds(200), _router.Statuses([a])[0].CooldownUntil);
        Assert.Equal(3, _router.Statuses([a])[0].FailuresInARow);

        // A success resets the count, so the next failure cools down for the base time again.
        _time.Now = _time.Now.AddSeconds(201);
        _server.Enqueue("a", Replies.Answer(BackendKind.OpenAi, "a ok"), Replies.Status(500));
        Assert.Equal("a", (await CompleteAsync(a, b)).Backend);
        Assert.Equal(0, _router.Statuses([a])[0].FailuresInARow);
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b5"));
        await CompleteAsync(a, b);
        Assert.Equal(_time.Now.AddSeconds(60), _router.Statuses([a])[0].CooldownUntil);
    }

    [Fact]
    public async Task RetryAfterIsHonoredWhenLongerThanTheCooldown()
    {
        _server.Enqueue("a", Replies.Status(429, retryAfter: "900"));
        _server.Enqueue("b", Replies.Answer(BackendKind.OpenAi, "b"));
        await CompleteAsync(Backend("a"), Backend("b"));
        Assert.Equal(_time.Now.AddSeconds(900), _router.Statuses([Backend("a")])[0].CooldownUntil);
    }

    [Fact]
    public async Task EveryBackendFailingReportsTheirStates()
    {
        _server.Enqueue("a", Replies.Status(500));
        _server.Enqueue("b", Replies.Status(429));
        var failure = await Assert.ThrowsAsync<AllBackendsFailed>(() =>
            CompleteAsync(Backend("a"), Backend("b"))
        );
        Assert.All(failure.States, state => Assert.NotNull(state.CooldownUntil));
        Assert.Contains("HTTP 429", failure.States[1].LastError);
    }

    [Fact]
    public async Task BadRequestIsNotAFailover()
    {
        _server.Enqueue("a", Replies.Status(400));
        await Assert.ThrowsAsync<BackendRejected>(() => CompleteAsync(Backend("a"), Backend("b")));
        Assert.Empty(_server.BodiesOf("b"));
    }

    [Fact]
    public async Task ParallelLimitQueuesRequestsPerBackend()
    {
        var a = Backend("a") with { Parallel = 1, FirstByteTimeout = 10 };
        var release = new TaskCompletionSource();
        var inFlight = 0;
        var most = 0;
        StubReply slow = async (request, response, token) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref inFlight));
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref inFlight);
            await Replies.Answer(BackendKind.OpenAi, "x")(request, response, token);
        };
        _server.Enqueue("a", slow, slow);
        var first = CompleteAsync(a);
        var second = CompleteAsync(a);
        await Task.Delay(300);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task UsageCountsCachedTokens()
    {
        _server.Enqueue(
            "o",
            Replies.Answer(BackendKind.OpenAi, "x", promptTokens: 1000, cachedTokens: 800)
        );
        _server.Enqueue(
            "c",
            Replies.Answer(BackendKind.Anthropic, "x", promptTokens: 1000, cachedTokens: 800)
        );
        Assert.Equal(
            new TokenUsage(1000, 800, 20),
            (await CompleteAsync(Backend("o"))).Response.Usage
        );
        Assert.Equal(
            new TokenUsage(1000, 800, 20),
            (await CompleteAsync(Backend("c", BackendKind.Anthropic))).Response.Usage
        );
    }

    [Fact]
    public async Task AnthropicRequestsCarryCacheBreakpointsAndTheKey()
    {
        _server.Enqueue("c", Replies.Answer(BackendKind.Anthropic, "x"));
        await CompleteAsync(Backend("c", BackendKind.Anthropic));
        var body = _server.BodiesOf("c")[0];
        Assert.Equal(
            "ephemeral",
            body.GetProperty("system")[0]
                .GetProperty("cache_control")
                .GetProperty("type")
                .GetString()
        );
        var messages = body.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1].GetProperty("content");
        Assert.Equal(
            "ephemeral",
            last[last.GetArrayLength() - 1]
                .GetProperty("cache_control")
                .GetProperty("type")
                .GetString()
        );
    }
}
