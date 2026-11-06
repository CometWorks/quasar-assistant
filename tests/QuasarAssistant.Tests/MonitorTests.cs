using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Monitoring;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

public sealed class MonitorTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    private sealed class Fleet
    {
        public string State = "Crashed";
    }

    private static List<QuasarOperation> Operations(Fleet fleet) =>
        [
            new TestOperation(
                "servers.list",
                new QuasarOperationMetadata { Effect = QuasarOperationEffect.Read }
            )
            {
                Handler = _ =>
                    new[]
                    {
                        new
                        {
                            uniqueName = "alpha",
                            kind = "standalone",
                            goal = "On",
                            state = fleet.State,
                            health = "Healthy",
                        },
                    },
            },
            TestOperation.Read("servers.status"),
            TestOperation.Change("servers.restart"),
        ];

    private static async Task<(Harness Harness, ManualTime Time)> StartAsync(
        Fleet fleet,
        Func<MonitorSettings, MonitorSettings>? change = null
    )
    {
        var time = new ManualTime();
        var harness = await Harness.StartAsync(Operations(fleet), time);
        harness.Configure(settings =>
            settings.WithRole(
                AssistantRoles.Monitor,
                settings.Role(AssistantRoles.Monitor) with
                {
                    Backends = ["main"],
                    OperationModes = new() { ["quasar.core/servers.restart"] = ToolMode.Apply },
                }
            ) with
            {
                Backends = [harness.Backend("main", Kind)],
                Monitor = (change ?? (m => m))(
                    new MonitorSettings
                    {
                        Enabled = true,
                        GraceMinutes = 5,
                        RecheckMinutes = 60,
                    }
                ),
            }
        );
        return (harness, time);
    }

    private static Task CheckAsync(Harness harness) =>
        harness
            .Services.GetRequiredService<MonitorService>()
            .CheckAsync(harness.Settings.Current.Monitor, default);

    private static IncidentStore Incidents(Harness harness) =>
        harness.Services.GetRequiredService<IncidentStore>();

    [Fact]
    public async Task ProblemRunsTheModelOnlyAfterTheGraceTimeAndResolves()
    {
        var fleet = new Fleet();
        var (harness, time) = await StartAsync(fleet);
        await using var _ = harness;
        harness.Server.Enqueue(
            "main",
            Replies.Answer(
                Kind,
                "Restarting.",
                [new ToolCall("c1", "quasar_core__servers_restart", "{\"uniqueName\":\"alpha\"}")]
            ),
            Replies.Answer(Kind, "Fixed: restarted alpha after a crash.")
        );

        await CheckAsync(harness);
        Assert.Empty(Incidents(harness).All);
        Assert.Empty(harness.Server.BodiesOf("main"));

        time.Now = time.Now.AddMinutes(5);
        await CheckAsync(harness);
        var incident = Assert.Single(Incidents(harness).All);
        Assert.Equal("down:alpha", incident.Key);
        var run = Assert.Single(incident.Runs);
        Assert.Equal("ok", run.Outcome);
        Assert.Equal("Fixed: restarted alpha after a crash.", run.Answer);
        Assert.Equal(240, run.Tokens);
        Assert.Equal(["quasar_core__servers_restart"], run.Tools);
        // The restart ran at once, as the monitor, acting for nobody.
        var restart = harness.Dispatched.Calls.Single(c => c.Key == "quasar.core/servers.restart");
        Assert.True(restart.User.IsQuasarAgent(out var agent));
        Assert.Equal(QuasarAgentKind.Monitor, agent.Kind);
        Assert.Null(agent.Asker);
        // The model got the monitor's prompt, and the problem with the server as context.
        var first = harness.Server.BodiesOf("main")[0].GetProperty("messages");
        Assert.Contains("You are the monitor", first[0].GetProperty("content").GetString());
        var ask = first[1].GetProperty("content").GetString()!;
        Assert.Contains("server: alpha", ask);
        Assert.Contains(
            "A check found a problem: The server alpha should run but is Crashed.",
            ask
        );

        // Still there, but within the recheck time: no new run.
        time.Now = time.Now.AddMinutes(1);
        await CheckAsync(harness);
        Assert.Single(Incidents(harness).All.Single().Runs);

        fleet.State = "Running";
        await CheckAsync(harness);
        Assert.NotNull(Incidents(harness).All.Single().Resolved);
    }

    [Fact]
    public async Task UsedUpBudgetSkipsTheModelAndTellsTheAdminsOnce()
    {
        var fleet = new Fleet();
        var (harness, time) = await StartAsync(
            fleet,
            m => m with { GraceMinutes = 0, BudgetTokens = 100 }
        );
        await using var _ = harness;
        harness.Configure(s =>
            s with
            {
                Notifications = s.Notifications with { Discord = true },
                Discord = s.Discord with { AdminChannelId = "42" },
            }
        );
        harness
            .Services.GetRequiredService<MetricsLog>()
            .Append(
                new RunMetrics
                {
                    Time = time.Now,
                    Role = AssistantRoles.Monitor,
                    Outcome = "ok",
                    PromptTokens = 1000,
                    CachedTokens = 950,
                    CompletionTokens = 60,
                }
            );
        // An admin's run doesn't count against the monitor.
        harness
            .Services.GetRequiredService<MetricsLog>()
            .Append(
                new RunMetrics
                {
                    Time = time.Now,
                    Role = AssistantRoles.Admin,
                    PromptTokens = 99999,
                }
            );
        Assert.Equal(110, harness.Services.GetRequiredService<MonitorBudget>().Status().Spent);

        await CheckAsync(harness);
        time.Now = time.Now.AddHours(2);
        await CheckAsync(harness);

        Assert.Empty(harness.Server.BodiesOf("main"));
        var runs = Incidents(harness).All.Single().Runs;
        Assert.Equal(["budget", "budget"], runs.Select(r => r.Outcome));
        Assert.Single(harness.Discord.Posts, post => post.Text.Contains("token budget"));
    }

    [Fact]
    public async Task RunStopsBeforeTheTurnThatWouldPassTheBudget()
    {
        var fleet = new Fleet();
        var (harness, _) = await StartAsync(
            fleet,
            m => m with { GraceMinutes = 0, BudgetTokens = 100 }
        );
        await using var __ = harness;
        harness.Server.Enqueue(
            "main",
            Replies.Answer(
                Kind,
                "Checking.",
                [new ToolCall("c1", "quasar_core__servers_status", "{}")]
            ),
            Replies.Answer(Kind, "Never asked.")
        );

        await CheckAsync(harness);

        Assert.Single(harness.Server.BodiesOf("main"));
        var run = Incidents(harness).All.Single().Runs.Single();
        Assert.Equal("budget", run.Outcome);
        Assert.Equal(120, run.Tokens);
    }

    [Fact]
    public void BudgetPeriodsFollowTheUtcCalendar()
    {
        var wednesday = new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.FromHours(-2));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero),
            MonitorBudget.PeriodStart(BudgetPeriod.Day, wednesday)
        );
        Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
            MonitorBudget.PeriodStart(BudgetPeriod.Week, wednesday)
        );
        var month = MonitorBudget.PeriodStart(BudgetPeriod.Month, wednesday);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), month);
        Assert.Equal(
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            MonitorBudget.NextStart(BudgetPeriod.Month, month)
        );
    }

    [Fact]
    public void CostUsesTheCachedPriceForCacheReads()
    {
        var backend = new BackendSettings
        {
            InputPrice = 2,
            CachedInputPrice = 0.2,
            OutputPrice = 10,
        };
        var run = new RunMetrics
        {
            PromptTokens = 1_000_000,
            CachedTokens = 900_000,
            CompletionTokens = 10_000,
        };
        Assert.Equal(0.2 + 0.18 + 0.1, MonitorBudget.Cost(backend, run)!.Value, 6);
        Assert.Null(MonitorBudget.Cost(new BackendSettings(), run));
    }

    [Fact]
    public async Task ScriptLinesAreProblemsKeyedBeforeTheColon()
    {
        if (OperatingSystem.IsWindows())
            return;
        var problems = await MonitorChecks.ScriptAsync(
            new MonitorSettings
            {
                ScriptCommand = "/bin/sh",
                ScriptArguments = "-c \"echo 'disk: 3 GB free'; echo; echo 'backup missed'\"",
            },
            default
        );
        Assert.Equal(["script:disk", "script:backup missed"], problems.Select(p => p.Key));
        Assert.Equal("disk: 3 GB free", problems[0].Text);

        var failed = await MonitorChecks.ScriptAsync(
            new MonitorSettings
            {
                ScriptCommand = "/bin/sh",
                ScriptArguments = "-c \"echo oops >&2; exit 3\"",
            },
            default
        );
        Assert.Equal("The monitor script exited with code 3: oops", Assert.Single(failed).Text);

        var slow = await MonitorChecks.ScriptAsync(
            new MonitorSettings
            {
                ScriptCommand = "/bin/sh",
                ScriptArguments = "-c \"sleep 10\"",
                ScriptTimeoutSeconds = 1,
            },
            default
        );
        Assert.Contains("longer than 1 s", Assert.Single(slow).Text);
    }
}
