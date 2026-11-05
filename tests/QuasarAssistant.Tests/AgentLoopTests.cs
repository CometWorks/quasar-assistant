using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

public sealed class AgentLoopTests
{
    private const string Status = "quasar_core__servers_status";
    private const string Start = "quasar_core__servers_start";
    private const string Delete = "quasar_core__servers_delete";
    private const BackendKind Kind = BackendKind.OpenAi;

    private static List<QuasarOperation> Operations() =>
        [
            new TestOperation(
                "servers.status",
                new QuasarOperationMetadata { Effect = QuasarOperationEffect.Read }
            )
            {
                Handler = _ => new
                {
                    name = "alpha",
                    running = true,
                    rconPassword = "hunter2",
                },
            },
            TestOperation.Change("servers.start"),
            TestOperation.Change("servers.delete"),
        ];

    private static StubReply Call(
        string tool,
        string arguments = "{\"server\":\"alpha\"}",
        string id = "c1"
    ) => Replies.Answer(Kind, "Let me check.", [new ToolCall(id, tool, arguments)]);

    private static List<ChatMessage> ToolResults(Conversation conversation) =>
        conversation.Messages.Where(m => m.Role == ChatRole.Tool).ToList();

    [Fact]
    public async Task ReadToolRunsAndItsResultGoesBackToTheModel()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.Server.Enqueue("main", Call(Status), Replies.Answer(Kind, "Alpha is running."));

        var conversation = await harness.AskAsync("Is alpha up?");

        Assert.Equal(["quasar.core/servers.status"], harness.Dispatched.Keys);
        var result = Assert.Single(ToolResults(conversation));
        Assert.False(result.IsError);
        Assert.Contains("\"running\":true", result.Text);
        Assert.Contains("<redacted>", result.Text);
        Assert.DoesNotContain("hunter2", result.Text);
        Assert.Equal("Alpha is running.", conversation.Messages[^1].Text);
        // The second request carried the tool result.
        var second = harness.Server.BodiesOf("main")[1].GetProperty("messages");
        Assert.Equal("tool", second[second.GetArrayLength() - 1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ChangeInProposeModeIsOnlyProposed()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.Server.Enqueue("main", Call(Start), Replies.Answer(Kind, "I proposed it."));

        var conversation = await harness.AskAsync("Start alpha");

        Assert.Empty(harness.Dispatched.Calls);
        var (key, arguments, reason) = Assert.Single(harness.Audit.Proposals);
        Assert.Equal("quasar.core/servers.start", key);
        Assert.Equal("{\"server\":\"alpha\"}", arguments);
        Assert.Equal("Let me check.", reason);
        var result = Assert.Single(ToolResults(conversation));
        Assert.StartsWith("Proposal entry1 created", result.Text);
        Assert.Equal("entry1", result.ProposalId);
        Assert.Single(harness.Services.GetRequiredService<ProposalStore>().Pending);
    }

    [Fact]
    public async Task ChangeInApplyModeIsDispatched()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.ConfigureRole(role =>
            role with
            {
                AreaModes = new() { ["servers"] = ToolMode.Apply },
            }
        );
        harness.Server.Enqueue("main", Call(Start), Replies.Answer(Kind, "Started."));

        await harness.AskAsync("Start alpha");

        Assert.Equal(["quasar.core/servers.start"], harness.Dispatched.Keys);
        Assert.Empty(harness.Audit.Proposals);
        // The call runs as the assistant principal built for the asker.
        Assert.Equal("Ada", harness.Dispatched.Calls.Single().User.FindFirst("asker")!.Value);
    }

    [Fact]
    public async Task RefusedCallGoesBackAsAnError()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.ConfigureRole(role =>
            role with
            {
                OperationModes = new() { ["quasar.core/servers.start"] = ToolMode.Apply },
            }
        );
        harness.Dispatched.Refuse["quasar.core/servers.start"] =
            "The person asking the assistant cannot access this SDK operation.";
        harness.Server.Enqueue("main", Call(Start), Replies.Answer(Kind, "You can't do that."));

        var conversation = await harness.AskAsync("Start alpha");

        var result = Assert.Single(ToolResults(conversation));
        Assert.True(result.IsError);
        Assert.Contains("cannot access this SDK operation", result.Text);
        var second = harness.Server.BodiesOf("main")[1].GetProperty("messages");
        Assert.Contains(
            "cannot access",
            second[second.GetArrayLength() - 1].GetProperty("content").GetString()
        );
    }

    [Fact]
    public async Task InvalidArgumentsAreAnErrorNotADispatch()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.Server.Enqueue("main", Call(Status, "{not json"), Replies.Answer(Kind, "Oops."));
        var conversation = await harness.AskAsync("Status");
        Assert.Empty(harness.Dispatched.Calls);
        Assert.True(Assert.Single(ToolResults(conversation)).IsError);
    }

    [Fact]
    public async Task StopsAtTheTurnLimit()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.ConfigureRole(role => role with { MaxTurns = 3 });
        harness.Server.Enqueue(
            "main",
            Call(Status, id: "1"),
            Call(Status, id: "2"),
            Call(Status, id: "3")
        );

        var conversation = await harness.AskAsync("Loop");

        Assert.Equal(3, harness.Server.BodiesOf("main").Count);
        Assert.Equal(ChatRole.Error, conversation.Messages[^1].Role);
        Assert.Contains("3 model turns", conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task StopsAtTheDeadline()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.ConfigureRole(role => role with { RunDeadlineSeconds = 1 });
        harness.Server.Enqueue("main", Replies.Silent());

        var conversation = await harness.AskAsync("Slow");

        Assert.Equal(ChatRole.Error, conversation.Messages[^1].Role);
        Assert.Contains("deadline", conversation.Messages[^1].Text);
    }

    [Fact]
    public async Task EveryBackendFailingShowsTheStatesInTheChat()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "a", "b");
        harness.Server.Enqueue("a", Replies.Status(500));
        harness.Server.Enqueue("b", Replies.Status(429));

        var conversation = await harness.AskAsync("Hello");

        var error = conversation.Messages[^1];
        Assert.Equal(ChatRole.Error, error.Role);
        Assert.Contains("- a: cooling down until", error.Text);
        Assert.Contains("- b: cooling down until", error.Text);
        Assert.Contains("HTTP 429", error.Text);
    }

    [Fact]
    public async Task QueueWaitCancelsARequestThatWaitsTooLong()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.ConfigureRole(role =>
            role with
            {
                MaxConcurrentRuns = 1,
                QueueWaitSeconds = 1,
                RunDeadlineSeconds = 10,
            }
        );
        var release = new TaskCompletionSource();
        harness.Server.Enqueue(
            "main",
            async (request, response, token) =>
            {
                await release.Task.WaitAsync(token);
                await Replies.Answer(Kind, "first")(request, response, token);
            }
        );

        var first = harness.AskAsync("first");
        await Task.Delay(200);
        var second = await harness.AskAsync("second", Harness.Person("Bob", "steam:2"));
        release.SetResult();
        await first;

        Assert.Contains("waited 1 s", second.Messages[^1].Text);
        Assert.Single(harness.Server.BodiesOf("main"));
    }

    [Fact]
    public async Task MetricsAreWrittenPerRun()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "a", "b");
        harness.Server.Enqueue("a", Replies.Status(500));
        harness.Server.Enqueue(
            "b",
            Replies.Answer(Kind, "hi", promptTokens: 500, cachedTokens: 300)
        );

        await harness.AskAsync("Hello");

        var line = File.ReadAllLines(Path.Combine(harness.Folder, "metrics.jsonl")).Single();
        var run = JsonSerializer.Deserialize<RunMetrics>(
            line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal("ok", run.Outcome);
        Assert.Equal(["b"], run.Backends);
        Assert.Equal(1, run.Fallovers);
        Assert.Equal(300, run.CachedTokens);
        var summary = harness
            .Services.GetRequiredService<MetricsLog>()
            .Summarize(DateTimeOffset.MinValue);
        Assert.Equal(1, summary.Backends.Single(b => b.Backend == "a").Failures);
    }

    [Fact]
    public async Task ConversationsAreSavedPerAskerAndExpire()
    {
        await using var harness = await Harness.StartAsync(Operations());
        harness.UseBackends(Kind, "main");
        harness.Server.Enqueue("main", Call(Status), Replies.Answer(Kind, "Up."));
        var conversation = await harness.AskAsync("Is alpha up?");

        var time = new ManualTime { Now = DateTimeOffset.UtcNow };
        var reopened = new ConversationStore(new AssistantPaths(harness.Folder), time);
        var loaded = reopened.Get("steam:1", conversation.Id)!;
        Assert.Equal(
            conversation.Messages.Select(m => (m.Role, m.Text, m.ToolCallId)),
            loaded.Messages.Select(m => (m.Role, m.Text, m.ToolCallId))
        );
        Assert.Null(reopened.Get("steam:2", conversation.Id));
        Assert.Single(reopened.List("steam:1"));

        time.Now = time.Now.AddDays(31);
        reopened.Expire(30);
        Assert.Empty(
            new ConversationStore(new AssistantPaths(harness.Folder), time).List("steam:1")
        );
    }
}
