using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Monitoring;

/// <param name="Spent">Uncached input and output tokens of the monitor's runs since <paramref name="Start"/>.</param>
/// <param name="SpentUsd">What those runs cost, all tokens counted; null when a backend that answered has no prices.</param>
/// <param name="LimitUsd">The budget in money at the input/output mix of this period's runs, or of 90% input
/// when there were none; null without prices.</param>
public sealed record BudgetStatus(
    BudgetPeriod Period,
    DateTimeOffset Start,
    DateTimeOffset Resets,
    long Limit,
    long Spent,
    double? SpentUsd,
    double? LimitUsd
)
{
    public long Left => Math.Max(0, Limit - Spent);
}

/// <summary>The monitor's token budget per UTC calendar day, week (from Monday) or month, counted from
/// <c>metrics.jsonl</c>.</summary>
public sealed class MonitorBudget(SettingsStore settings, MetricsLog metrics, TimeProvider time)
{
    public BudgetStatus Status()
    {
        var current = settings.Current;
        var monitor = current.Monitor;
        var start = PeriodStart(monitor.BudgetPeriod, time.GetUtcNow());
        var runs = metrics.Runs(start).Where(run => run.Role == AssistantRoles.Monitor).ToList();
        long spent = 0;
        double? spentUsd = 0;
        foreach (var run in runs)
        {
            spent += Uncached(run);
            if (Uncached(run) + run.CachedTokens == 0)
                continue;
            var backend = current.Backends.FirstOrDefault(b =>
                b.Name == run.Backends.FirstOrDefault()
            );
            spentUsd = Cost(backend, run) is { } cost ? spentUsd + cost : null;
        }
        double? perToken =
            spent > 0 && spentUsd is { } usd ? usd / spent
            : current.BackendsOf(AssistantRoles.Monitor).FirstOrDefault()
                is { InputPrice: { } input, OutputPrice: { } output }
                ? (0.9 * input + 0.1 * output) / 1e6
            : null;
        return new(
            monitor.BudgetPeriod,
            start,
            NextStart(monitor.BudgetPeriod, start),
            monitor.BudgetTokens,
            spent,
            spentUsd,
            perToken * monitor.BudgetTokens
        );
    }

    public static long Uncached(RunMetrics run) =>
        (long)run.PromptTokens - run.CachedTokens + run.CompletionTokens;

    /// <summary>A run's cost in USD at the backend's prices; cache reads at the cached price, or the input price.</summary>
    public static double? Cost(BackendSettings? backend, RunMetrics run) =>
        backend is { InputPrice: { } input, OutputPrice: { } output }
            ? (
                (run.PromptTokens - run.CachedTokens) * input
                + run.CachedTokens * (backend.CachedInputPrice ?? input)
                + run.CompletionTokens * output
            ) / 1e6
            : null;

    public static DateTimeOffset PeriodStart(BudgetPeriod period, DateTimeOffset now)
    {
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return period switch
        {
            BudgetPeriod.Day => day,
            BudgetPeriod.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            _ => new DateTimeOffset(day.Year, day.Month, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }

    public static DateTimeOffset NextStart(BudgetPeriod period, DateTimeOffset start) =>
        period switch
        {
            BudgetPeriod.Day => start.AddDays(1),
            BudgetPeriod.Week => start.AddDays(7),
            _ => start.AddMonths(1),
        };
}
