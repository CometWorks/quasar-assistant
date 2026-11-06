using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Monitoring;

/// <param name="Key">Stable while the problem lasts, such as <c>down:alpha</c>.</param>
/// <param name="Server">The standalone server or cluster it concerns, if any.</param>
public sealed record MonitorProblem(string Key, string Text, string? Server = null);

/// <summary>
/// The checks behind the monitor, run without a model: core's read operations as the monitor principal, and the
/// admins' script. A server or cluster counts as down or unhealthy only after it stayed so for the grace time.
/// </summary>
public sealed class MonitorChecks(
    IEnumerable<QuasarOperation> operations,
    IQuasarAgentPrincipals principals,
    IServiceScopeFactory scopes,
    TimeProvider time
)
{
    private const string Core = "quasar.core";
    private readonly Dictionary<string, DateTimeOffset> _since = [];

    /// <summary>Why the last check couldn't read core, or null.</summary>
    public string? LastError { get; private set; }

    public async Task<List<MonitorProblem>> RunAsync(
        MonitorSettings settings,
        CancellationToken token
    )
    {
        var now = time.GetUtcNow();
        List<MonitorProblem> found = [];
        HashSet<string> seen = [];
        void Lasting(string key, string text, string server, int minutes)
        {
            seen.Add(key);
            if (!_since.TryGetValue(key, out var since))
                _since[key] = since = now;
            if (now - since >= TimeSpan.FromMinutes(minutes))
                found.Add(new(key, $"{text} Since {Utc(since)}.", server));
        }

        var agent = principals.CreateMonitor("monitor-checks", "monitor checks");
        var servers = await ReadAsync<List<ServerItem>>(agent, "servers.list", new { }, token);
        LastError = servers.Error;
        foreach (var server in servers.Value ?? [])
        {
            if (server.Goal != "On")
                continue;
            var name = server.UniqueName;
            if (server.Kind == "cluster")
            {
                var status = await ReadAsync<ClusterItem>(
                    agent,
                    "cluster.status",
                    new { uniqueName = name },
                    token
                );
                if (status.Value is not { GatewayError: null, Health: not null } cluster)
                    Lasting(
                        "cluster-unreachable:" + name,
                        $"The cluster {name} should run, but its Gateway can't be read: {status.Value?.GatewayError ?? status.Error}.",
                        name,
                        settings.GraceMinutes
                    );
                else if (cluster.Health != "Healthy")
                    Lasting(
                        "cluster-unhealthy:" + name,
                        $"The cluster {name} is {cluster.Health} in phase {cluster.Phase}. Reasons: {string.Join(", ", [.. cluster.ReasonCodes ?? [], .. cluster.DegradedReasons ?? []])}.",
                        name,
                        settings.GraceMinutes
                    );
                continue;
            }
            if (server.State is "Stopped" or "Crashed" or "Faulted")
                Lasting(
                    "down:" + name,
                    $"The server {name} should run but is {server.State}.",
                    name,
                    settings.GraceMinutes
                );
            else if (server.Health == "Unhealthy")
                Lasting(
                    "unhealthy:" + name,
                    $"The server {name} is unhealthy.",
                    name,
                    settings.GraceMinutes
                );
            if (server.State != "Running")
                continue;
            if (settings.MinSimSpeed > 0 && server.SimSpeed < settings.MinSimSpeed)
                Lasting(
                    "slow:" + name,
                    $"The server {name} runs at sim speed {server.SimSpeed:0.00}, below {settings.MinSimSpeed:0.00}.",
                    name,
                    settings.SlowMinutes
                );
            var maxAge = TimeSpan.FromMinutes(settings.MaxSaveAgeMinutes);
            if (
                settings.MaxSaveAgeMinutes > 0
                && server.LastSaveUtc is { } saved
                && now - saved > maxAge
                && server.UptimeSeconds > maxAge.TotalSeconds
            )
                found.Add(
                    new(
                        "no-save:" + name,
                        $"The server {name} hasn't saved its world since {Utc(saved)}.",
                        name
                    )
                );
        }
        // A read error keeps the times, so a short outage of core doesn't restart the grace.
        if (servers.Error is null)
            foreach (var key in _since.Keys.Except(seen).ToList())
                _since.Remove(key);

        if (!string.IsNullOrWhiteSpace(settings.ScriptCommand))
            found.AddRange(await ScriptAsync(settings, token));
        return found;
    }

    /// <summary>Runs the admins' script. Each line it prints is a problem; the text before the first ": " is the
    /// key, or the whole line without one.</summary>
    internal static async Task<List<MonitorProblem>> ScriptAsync(
        MonitorSettings settings,
        CancellationToken token
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(settings.ScriptCommand, settings.ScriptArguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return [new("script", "The monitor script can't start: " + error.Message)];
        }
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.ScriptTimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            token.ThrowIfCancellationRequested();
            return
            [
                new(
                    "script",
                    $"The monitor script ran longer than {settings.ScriptTimeoutSeconds} s and was stopped."
                ),
            ];
        }
        var problems = (await output)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(50)
            .Select(line =>
            {
                var cut = line.IndexOf(": ", StringComparison.Ordinal);
                var key = cut > 0 ? line[..cut] : line;
                return new MonitorProblem("script:" + Cut(key, 200), Cut(line, 1000));
            })
            .DistinctBy(problem => problem.Key)
            .ToList();
        if (process.ExitCode != 0 && problems.Count == 0)
            problems.Add(
                new(
                    "script",
                    $"The monitor script exited with code {process.ExitCode}: {Cut((await errors).Trim(), 500)}"
                )
            );
        return problems;
    }

    private async Task<(T? Value, string? Error)> ReadAsync<T>(
        ClaimsPrincipal agent,
        string operationId,
        object request,
        CancellationToken token
    )
    {
        var operation = operations.FirstOrDefault(op =>
            op.PluginId == Core && op.OperationId == operationId
        );
        if (operation is null)
            return (default, $"{operationId} isn't registered");
        try
        {
            var result = await ToolExecutor.DispatchAsync(
                scopes,
                agent,
                operation,
                JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web),
                token
            );
            if (result.StatusCode != 200)
                return (default, $"{operationId}: {result.StatusCode} {result.Error}");
            return (
                JsonSerializer.Deserialize<T>(
                    JsonSerializer.SerializeToElement(result.Value, JsonSerializerOptions.Web),
                    JsonSerializerOptions.Web
                ),
                null
            );
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (default, $"{operationId}: {error.Message}");
        }
    }

    private static string Utc(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";

    private sealed record ServerItem(
        string UniqueName,
        string Kind,
        string Goal,
        string? State,
        string? Health,
        int? UptimeSeconds,
        float? SimSpeed,
        DateTimeOffset? LastSaveUtc
    );

    private sealed record ClusterItem(
        string? GatewayError,
        string? Phase,
        string? Health,
        List<string>? ReasonCodes,
        List<string>? DegradedReasons
    );
}
