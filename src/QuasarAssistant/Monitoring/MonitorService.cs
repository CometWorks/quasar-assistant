using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Monitoring;

/// <summary>
/// Runs the checks every few seconds while the monitor is on. A new problem opens an incident and gets one model
/// run in the monitor role, a problem still there gets another after the recheck time, and a problem gone
/// resolves its incident. Only those runs use the model, and only while the token budget lasts.
/// </summary>
public sealed class MonitorService(
    SettingsStore settings,
    MonitorChecks checks,
    MonitorBudget budget,
    IncidentStore incidents,
    ConversationStore conversations,
    AssistantRunner runner,
    IQuasarAgentPrincipals principals,
    AdminNotifier notifier,
    TimeProvider time,
    ILogger<MonitorService> logger
) : BackgroundService
{
    public const string Subject = "agent:monitor";
    public const string Name = "Monitor";

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var monitor = settings.Current.Monitor;
            if (monitor.Enabled)
                try
                {
                    await CheckAsync(monitor, token);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning(error, "Monitor check failed");
                }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, monitor.CheckSeconds)), time, token);
        }
    }

    public async Task CheckAsync(MonitorSettings monitor, CancellationToken token)
    {
        var problems = await checks.RunAsync(monitor, token);
        var now = time.GetUtcNow();
        var keys = problems.Select(problem => problem.Key).ToHashSet();
        foreach (var gone in incidents.All.Where(i => i.Resolved is null && !keys.Contains(i.Key)))
        {
            incidents.Update(gone.Id, incident => incident with { Resolved = now });
            await notifier.PostAsync($"Monitor: resolved. {gone.Text}", token);
        }
        foreach (var problem in problems)
        {
            var open = incidents.OpenFor(problem.Key);
            if (open is null)
            {
                open = new Incident
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Key = problem.Key,
                    Text = problem.Text,
                    Server = problem.Server,
                    Opened = now,
                    LastSeen = now,
                    ConversationId = conversations.Create(Subject, Name, AssistantRoles.Monitor).Id,
                };
                incidents.Add(open);
                await RunAsync(open, recheck: false, token);
                continue;
            }
            incidents.Update(
                open.Id,
                incident => incident with { Text = problem.Text, LastSeen = now }
            );
            var last = open.Runs.LastOrDefault()?.Time ?? open.Opened;
            if (now - last >= TimeSpan.FromMinutes(Math.Max(1, monitor.RecheckMinutes)))
                await RunAsync(incidents.Get(open.Id)!, recheck: true, token);
        }
    }

    private async Task RunAsync(Incident incident, bool recheck, CancellationToken token)
    {
        var now = time.GetUtcNow();
        var status = budget.Status();
        if (status.Left <= 0)
        {
            var text =
                $"No model run: the monitor's token budget of {status.Limit:N0} is used up until {status.Resets.UtcDateTime:yyyy-MM-dd HH:mm} UTC.";
            incidents.Update(
                incident.Id,
                i => i with { Runs = [.. i.Runs, new(now, "budget", text, 0, 0, [])] }
            );
            if (incidents.TryMarkBudgetNotice(status.Start))
                await notifier.PostAsync($"Monitor: {incident.Text}\n{text}", token);
            return;
        }
        var conversation =
            conversations.Get(Subject, incident.ConversationId)
            ?? conversations.Create(Subject, Name, AssistantRoles.Monitor, incident.ConversationId);
        var opened = (int)(now - incident.Opened).TotalMinutes;
        var request =
            (recheck ? $"Still there after {opened} minutes: " : "A check found a problem: ")
            + incident.Text;
        List<string> hostLines = [Prompt.Time(now), $"asker: {Name} ({Subject})"];
        if (incident.Server is { } server)
            hostLines.Add("server: " + server);
        var result = await runner.AskAsync(
            conversation,
            new AskRequest(request, hostLines)
            {
                Agent = principals.CreateMonitor(conversation.Id, request),
                TokenBudget = status.Left,
            },
            token
        );
        if (result is null)
            return; // Still running from before.
        var tokens = result.Usage.Uncached;
        var spent = budget.Status();
        var usd = spent.SpentUsd - status.SpentUsd;
        incidents.Update(
            incident.Id,
            i =>
                i with
                {
                    Runs =
                    [
                        .. i.Runs,
                        new(
                            now,
                            result.Outcome,
                            Cut(result.Text, 4000),
                            tokens,
                            usd,
                            [.. result.Tools]
                        ),
                    ],
                }
        );
        await notifier.PostAsync(
            $"Monitor: {incident.Text}\n{Cut(result.Text, 1500)}\n{notifier.MonitorUrl}",
            token
        );
    }

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";
}
