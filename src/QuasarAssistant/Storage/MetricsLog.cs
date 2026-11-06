using System.Text.Json;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Storage;

/// <summary>One line of <c>metrics.jsonl</c>: a run, answered or not. It holds no message text.</summary>
public sealed record RunMetrics
{
    public DateTimeOffset Time { get; init; }
    public string Role { get; init; } = "";

    /// <summary><c>ok</c>, <c>busy</c> (queue wait passed), <c>deadline</c>, <c>turn-limit</c>, <c>budget</c>,
    /// <c>backends-failed</c>, <c>rejected</c>, <c>stopped</c> or <c>error</c>.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>The backends that answered a turn.</summary>
    public List<string> Backends { get; init; } = [];

    /// <summary>Moves to the next backend within a turn.</summary>
    public int Fallovers { get; init; }
    public List<BackendAttempt> Failures { get; init; } = [];
    public int Turns { get; init; }
    public List<string> Tools { get; init; } = [];
    public int PromptTokens { get; init; }
    public int CachedTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double QueueSeconds { get; init; }
    public double Seconds { get; init; }
}

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

public sealed class MetricsLog(AssistantPaths paths)
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "metrics.jsonl");
    private readonly object _lock = new();

    public void Append(RunMetrics run)
    {
        var line = JsonSerializer.Serialize(run, JsonFiles.Options) + "\n";
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line);
        }
    }

    public IReadOnlyList<RunMetrics> Runs(DateTimeOffset since)
    {
        lock (_lock)
            return File.Exists(_path)
                ? File.ReadLines(_path)
                    .Select(line => JsonSerializer.Deserialize<RunMetrics>(line, JsonFiles.Options))
                    .OfType<RunMetrics>()
                    .Where(run => run.Time >= since)
                    .ToList()
                : [];
    }

    public MetricsSummary Summarize(DateTimeOffset since)
    {
        var runs = Runs(since);
        var days = runs.GroupBy(run => DateOnly.FromDateTime(run.Time.UtcDateTime))
            .OrderByDescending(day => day.Key)
            .Select(day => new DayMetrics(
                day.Key,
                day.Count(),
                day.Count(run => run.Outcome != "ok"),
                day.Sum(run => (long)run.PromptTokens),
                day.Sum(run => (long)run.CachedTokens)
            ))
            .ToList();
        var names = runs.SelectMany(run => run.Backends.Concat(run.Failures.Select(f => f.Backend)))
            .Distinct()
            .Order();
        var backends = names
            .Select(name => new BackendMetrics(
                name,
                runs.Sum(run => run.Backends.Count(b => b == name)),
                runs.Sum(run =>
                    run.Failures.Count(f => f.Backend == name && f.Outcome == "failed")
                ),
                runs.Sum(run => run.Failures.Count(f => f.Backend == name))
            ))
            .ToList();
        return new MetricsSummary(days, backends);
    }
}
