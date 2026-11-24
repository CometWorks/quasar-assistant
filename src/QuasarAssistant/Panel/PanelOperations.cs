using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Plugin.Abstractions.Servers;
using QuasarAssistant.Agent;
using QuasarAssistant.Lore;
using QuasarAssistant.Monitoring;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Panel;

internal static class PanelOperations
{
    internal const string Use = QuasarPolicyNames.CanUseAssistant;
    internal const string Configure = QuasarPolicyNames.CanManageSecurity;
    internal const string Edit = QuasarPolicyNames.CanEditServers;
    internal const string Control = QuasarPolicyNames.CanControlServers;
    internal const string Plugin = AssistantServiceCollectionExtensions.PluginId;

    internal static QuasarOperationMetadata Metadata(bool change) =>
        new()
        {
            RequireDefaultPolicies = true,
            AgentAccess = QuasarAgentAccess.Denied,
            Effect = change ? QuasarOperationEffect.Change : QuasarOperationEffect.Read,
            Deadline = TimeSpan.FromMinutes(3),
        };

    internal static T Get<T>(this QuasarOperationContext c)
        where T : notnull => c.Services.GetRequiredService<T>();

    internal static string Subject(this QuasarOperationContext c) =>
        c.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } subject
            ? subject
            : throw new QuasarOperationException(403, "This action needs an identified account.");

    private static async Task<bool> Can(this QuasarOperationContext c, string policy) =>
        (await c.Get<IAuthorizationService>().AuthorizeAsync(c.User, null, policy)).Succeeded;

    internal static void Operation<T, Q>(
        this IServiceCollection services,
        string name,
        string policy,
        Func<QuasarOperationContext, T, CancellationToken, Task<Q>> handler,
        bool change = false
    ) =>
        services.AddQuasarOperation<T, Q>(
            Plugin,
            name,
            [policy],
            async (c, r, t) =>
            {
                try
                {
                    return await handler(c, r, t);
                }
                catch (ArgumentException e)
                {
                    throw new QuasarOperationException(400, e.Message);
                }
            },
            Metadata(change)
        );

    // The SDK audits the empty control request. Private messages and credential updates stay
    // in the worker-owned body and start changing state only after the SDK reauthorizes commit.
    internal static void Upload<T, TResult>(
        this IServiceCollection services,
        string name,
        string policy,
        long limit,
        Func<QuasarOperationContext, T, CancellationToken, Task<TResult>> commit
    ) =>
        services.AddQuasarUpload<SettingsReadRequest, TResult>(
            Plugin,
            name,
            [policy],
            limit,
            async (caller, _, body, token) =>
            {
                var input = await JsonSerializer.DeserializeAsync<T>(
                    body,
                    JsonSerializerOptions.Web,
                    token
                ) ?? throw new QuasarOperationException(400, "Missing input.");
                return new(async commitToken =>
                {
                    try
                    {
                        return await commit(caller, input, commitToken);
                    }
                    catch (ArgumentException error)
                    {
                        throw new QuasarOperationException(400, error.Message);
                    }
                });
            },
            Metadata(true)
        );

    internal static void AddPanel(this IServiceCollection s)
    {
        s.Operation<SettingsReadRequest, PanelStatus>("panel.status", Use, async (c, _, _) =>
        {
            var settings = c.Get<SettingsStore>().Current;
            return new(
                settings.BackendsOf(AssistantRoles.Admin).Count != 0,
                await c.Can(Configure)
            );
        });
        s.Operation<ItemId, ChatIndex>("chat.list", Use, (c, r, _) =>
            Task.FromResult(
                new ChatIndex(
                    [.. c.Get<ConversationStore>().List(c.Subject())],
                    c.Get<SettingsStore>().Current.ConversationRetentionDays,
                    !string.IsNullOrEmpty(r.Id)
                        && c.Get<ConversationStore>().Get(c.Subject(), r.Id) is null
                )
            )
        );
        s.Operation<ItemId, ChatState>("chat.read", Use, async (c, r, _) =>
        {
            var conversation = Owned(c, r.Id);
            var runner = c.Get<AssistantRunner>();
            List<ChatLine> lines = [];
            foreach (var m in conversation.Messages)
            {
                var proposal =
                    c.Get<SettingsStore>().Current.Notifications.Cards && m.ProposalId is { } id
                        ? c.Get<ProposalStore>().Get(id)
                        : null;
                lines.Add(
                    new(
                        m.Role.ToString(),
                        m.Text,
                        m.IsError,
                        m.ToolName,
                        m.ToolCalls?.Select(call => new CallLine(call.Name, call.Arguments))
                            .ToList(),
                        proposal is null ? null : await MakeCard(c, proposal)
                    )
                );
            }
            return new(lines, runner.IsRunning(r.Id), runner.Status(r.Id), runner.Draft(r.Id));
        });
        s.Upload<ChatSend, string>("chat.send", Use, 128 * 1024, async (c, r, _) =>
        {
            var text = r.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 8000)
                throw new ArgumentException("Give a message of 1 to 8000 characters.");
            var owner = c.Subject();
            var conversation = r.Id is null
                ? c.Get<ConversationStore>().Create(owner, c.User.Identity?.Name ?? owner)
                : Owned(c, r.Id);
            // AskAsync claims the run synchronously and handles completion independently of the view.
            var run = c.Get<AssistantRunner>()
                .AskAsync(conversation, c.User, text, new(r.PageUri, r.ServerName));
            if (run.IsCompleted && !await run)
                throw new QuasarOperationException(409, "This conversation is already running.");
            return conversation.Id;
        });
        s.Operation<ItemId, bool>(
            "chat.stop",
            Use,
            (c, r, _) =>
            {
                c.Get<AssistantRunner>().Stop(Owned(c, r.Id).Id);
                return Task.FromResult(true);
            },
            true
        );
        s.Operation<SettingsReadRequest, ApprovalList>("approvals.list", Use, async (c, _, _) =>
        {
            var proposals = c.Get<ProposalStore>().All;
            var handovers = c.Get<HandoverStore>().All;
            List<Card> cards = [];
            foreach (
                var p in proposals
                    .Where(p => p.Status == ProposalStatus.Pending)
                    .OrderBy(p => p.Created)
                    .Concat(
                        proposals
                            .Where(p => p.Status != ProposalStatus.Pending)
                            .OrderByDescending(p => p.Decided)
                            .Take(50)
                    )
            )
                cards.Add(await MakeCard(c, p));
            return new(
                cards,
                [
                    .. handovers.Where(h => !h.Handled).OrderBy(h => h.Created),
                    .. handovers.Where(h => h.Handled).OrderByDescending(h => h.HandledAt).Take(20),
                ],
                await c.Can(Control)
            );
        });
        s.Operation<Decision, bool>(
            "approvals.decide",
            Use,
            async (c, r, t) =>
            {
                var service = c.Get<ProposalService>();
                if (r.Reason?.Length > 500)
                    throw new ArgumentException("The reason exceeds 500 characters.");
                var result = r.Approve
                    ? await service.ApproveAsync(c.User, r.Id, t)
                    : await service.RejectAsync(c.User, r.Id, r.Reason, t);
                if (result is not null)
                    throw new QuasarOperationException(409, result);
                return true;
            },
            true
        );
        s.Operation<ItemId, bool>(
            "handovers.handle",
            Control,
            async (c, r, _) =>
            {
                if (!await c.Can(Use))
                    throw new QuasarOperationException(403, "Permission denied.");
                return c.Get<HandoverStore>()
                    .MarkHandled(
                        r.Id,
                        c.User.Identity?.Name ?? c.Subject(),
                        c.Get<TimeProvider>().GetUtcNow()
                    );
            },
            true
        );
        s.Operation<SettingsReadRequest, MonitorView>("monitor.read", Use, (c, _, _) =>
            Task.FromResult(
                new MonitorView(
                    c.Get<SettingsStore>().Current.Monitor.Enabled,
                    c.Get<MonitorChecks>().LastError,
                    c.Get<MonitorBudget>().Status(),
                    [
                        .. c.Get<IncidentStore>().All.Where(i => i.Resolved == null),
                        .. c.Get<IncidentStore>()
                            .All.Where(i => i.Resolved != null)
                            .OrderByDescending(i => i.Resolved)
                            .Take(50),
                    ]
                )
            )
        );
        s.Operation<SettingsReadRequest, LoreIndex>("lore.targets", Edit, (c, _, _) =>
        {
            var entries = c.Get<LoreStore>().List();
            var targets = (
                c.Services.GetService<IQuasarServerQueries>()
                    ?.GetServers()
                    .Select(x => x.UniqueName)
                ?? []
            )
                .Concat(entries.Select(x => x.Target))
                .DistinctBy(LoreStore.Key)
                .Order(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new LoreIndex([.. targets], [.. entries]));
        });
        s.AddQuasarSubscription<SettingsReadRequest, int>(
            Plugin,
            "panel.watch",
            [Use],
            (c, _, t) => Watch(c, t),
            Metadata(false)
        );
        s.AddSettingsPanel();
    }

    private static Conversation Owned(QuasarOperationContext c, string id) =>
        c.Get<ConversationStore>().Get(c.Subject(), id)
        ?? throw new QuasarOperationException(
            404,
            "This conversation doesn't exist or isn't yours."
        );

    private static async Task<Card> MakeCard(QuasarOperationContext c, Proposal p) =>
        new(
            p,
            p.Status == ProposalStatus.Pending
                && await c.Get<ProposalService>()
                    .CanApproveAsync(c.User, c.Get<IAuthorizationService>(), p)
        );

    // One subscription per mounted dashboard. Coalescing flags keeps slow clients bounded while
    // preserving changes from different stores. Conversation notifications are private to the owner.
    private static async IAsyncEnumerable<int> Watch(
        QuasarOperationContext c,
        [EnumeratorCancellation] CancellationToken token
    )
    {
        var subject = c.Subject();
        var conversations = c.Get<ConversationStore>();
        var proposals = c.Get<ProposalStore>();
        var handovers = c.Get<HandoverStore>();
        var incidents = c.Get<IncidentStore>();
        var settings = c.Get<SettingsStore>();
        var changes = Channel.CreateBounded<bool>(1);
        int pending = 0;
        void Signal(int topics)
        {
            Interlocked.Or(ref pending, topics);
            changes.Writer.TryWrite(true);
        }
        void ChatChanged(string id)
        {
            if (conversations.Get(subject, id) is not null)
                Signal(2);
        }
        void SettingsChanged() => Signal(1 | 8);
        void DecisionsChanged() => Signal(4);
        void MonitorChanged() => Signal(8);
        settings.Changed += SettingsChanged;
        conversations.Changed += ChatChanged;
        proposals.Changed += DecisionsChanged;
        handovers.Changed += DecisionsChanged;
        incidents.Changed += MonitorChanged;
        try
        {
            Signal(15);
            while (await changes.Reader.WaitToReadAsync(token))
            {
                changes.Reader.TryRead(out _);
                int topics = Interlocked.Exchange(ref pending, 0);
                if (topics != 0)
                    yield return topics;
                await Task.Delay(100, token);
            }
        }
        finally
        {
            settings.Changed -= SettingsChanged;
            conversations.Changed -= ChatChanged;
            proposals.Changed -= DecisionsChanged;
            handovers.Changed -= DecisionsChanged;
            incidents.Changed -= MonitorChanged;
        }
    }
}
