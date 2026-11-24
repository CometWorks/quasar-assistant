using QuasarAssistant.Settings;
namespace QuasarAssistant.Monitoring;

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
