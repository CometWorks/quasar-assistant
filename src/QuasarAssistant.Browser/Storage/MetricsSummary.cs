namespace QuasarAssistant.Storage;

public sealed record DayMetrics(
    DateOnly Day,
    int Runs,
    int Failed,
    long PromptTokens,
    long CachedTokens
);

public sealed record BackendMetrics(string Backend, int Answers, int Failures, int Fallovers);

public sealed record MetricsSummary(
    IReadOnlyList<DayMetrics> Days,
    IReadOnlyList<BackendMetrics> Backends
);
