using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

/// <summary>An operation for the tests; the handler gets the request JSON.</summary>
public sealed class TestOperation(
    string operationId,
    QuasarOperationMetadata? metadata = null,
    QuasarOperationKind kind = QuasarOperationKind.Json,
    string[]? policies = null,
    string pluginId = "quasar.core"
) : QuasarOperation(pluginId, operationId, policies ?? [QuasarPolicyNames.CanView])
{
    public override QuasarOperationKind Kind => kind;

    public override QuasarOperationMetadata Metadata { get; } =
        (metadata ?? new()).ResolveFor(kind, operationId);

    public Func<JsonElement, object?> Handler { get; set; } = _ => new { ok = true };

    public override Task<object?> InvokeAsync(
        QuasarOperationContext context,
        JsonElement request,
        CancellationToken token
    ) => Task.FromResult(Handler(request));

    public static TestOperation Read(string id) =>
        new(id, new QuasarOperationMetadata { Effect = QuasarOperationEffect.Read });

    public static TestOperation Change(string id) =>
        new(id, new QuasarOperationMetadata { Reversal = QuasarReversal.NothingToReverse });
}

/// <summary>Records calls and, like core, refuses the agent deny list.</summary>
public sealed class DispatchLog
{
    public ConcurrentQueue<(string Key, string Request, ClaimsPrincipal User)> Calls { get; } =
        new();

    /// <summary>Operation keys refused for the asker, as core would for missing policies.</summary>
    public ConcurrentDictionary<string, string> Refuse { get; } = new();

    /// <summary>Operation keys core no longer offers to agents, as after an RBAC change.</summary>
    public ConcurrentBag<string> Unavailable { get; } = [];

    public IEnumerable<string> Keys => Calls.Select(call => call.Key);
}

/// <summary>Like core: the deny list and the current rules decide, here the AgentAccess flag, the security
/// prefix and the keys a test closes as an RBAC override would.</summary>
public sealed class FakeAgentOperations(IEnumerable<QuasarOperation> operations, DispatchLog log)
    : IQuasarAgentOperations
{
    public Task<bool> IsAvailableToAgentsAsync(string pluginId, string operationId) =>
        Task.FromResult(
            operations.FirstOrDefault(op => op.Key == pluginId + "/" + operationId) is { } operation
                && operation.Metadata.AgentAccess == QuasarAgentAccess.Allowed
                && !operationId.StartsWith("security.", StringComparison.Ordinal)
                && !log.Unavailable.Contains(operation.Key)
        );
}

public sealed class FakeDispatcher(IEnumerable<QuasarOperation> operations, DispatchLog log)
    : IQuasarOperationDispatcher
{
    private readonly Dictionary<string, QuasarOperation> _operations = operations.ToDictionary(op =>
        op.Key
    );

    public async Task<QuasarOperationResult> DispatchAsync(
        QuasarOperationContext context,
        string pluginId,
        string operationId,
        JsonElement request,
        CancellationToken cancellationToken
    )
    {
        var key = pluginId + "/" + operationId;
        log.Calls.Enqueue((key, request.GetRawText(), context.User));
        if (!_operations.TryGetValue(key, out var operation))
            return new(404, Error: "SDK operation not found.");
        if (
            operation.Metadata.AgentAccess == QuasarAgentAccess.Denied
            || operationId.StartsWith("security.", StringComparison.Ordinal)
        )
            return new(403, Error: "Assistants cannot use this operation.");
        if (log.Refuse.TryGetValue(key, out var reason))
            return new(403, Error: reason);
        return new(200, await operation.InvokeAsync(context, request, cancellationToken));
    }
}

public sealed class FakePrincipals : IQuasarAgentPrincipals
{
    public Task<ClaimsPrincipal> CreateAssistantAsync(
        ClaimsPrincipal asker,
        string conversationId,
        string request
    ) =>
        Task.FromResult(
            new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "agent:assistant"),
                        new Claim("asker", asker.Identity!.Name!),
                    ],
                    "fake"
                )
            )
        );
}

public sealed class FakeAuditLog : IQuasarAuditLog
{
    private int _next;

    public ConcurrentQueue<(string Key, string Arguments, string? Reason)> Proposals { get; } =
        new();

    public Task<QuasarAuditEntry> RecordProposalAsync(
        ClaimsPrincipal agent,
        string pluginId,
        string operationId,
        JsonElement arguments,
        string? reason,
        CancellationToken token
    )
    {
        Proposals.Enqueue((pluginId + "/" + operationId, arguments.GetRawText(), reason));
        return Task.FromResult(
            new QuasarAuditEntry
            {
                Id = "entry" + Interlocked.Increment(ref _next),
                Time = DateTimeOffset.UtcNow,
                Principal = new(QuasarAuditPrincipalKind.Assistant, "agent:assistant", "Assistant"),
                Requester = new(),
                Operation = pluginId + "/" + operationId,
                Area = "test",
                Mode = QuasarAuditMode.Proposed,
            }
        );
    }

    /// <summary>What core answers on approval.</summary>
    public QuasarOperationResult ApproveResult { get; set; } = new(200, new { done = true });
    public List<(string Id, string? Reason, bool Expired)> Rejected { get; } = [];

    public Task<QuasarOperationResult> ApproveAsync(
        ClaimsPrincipal approver,
        string proposalEntryId,
        CancellationToken token
    ) => Task.FromResult(ApproveResult);

    public Task<QuasarOperationResult> RejectAsync(
        ClaimsPrincipal? approver,
        string proposalEntryId,
        string? reason,
        CancellationToken token
    )
    {
        Rejected.Add((proposalEntryId, reason, approver is null));
        return Task.FromResult(new QuasarOperationResult(200));
    }

    public Task<IReadOnlyList<QuasarAuditEntry>> QueryAsync(
        QuasarAuditFilter filter,
        CancellationToken token
    ) => Task.FromResult<IReadOnlyList<QuasarAuditEntry>>([]);
}

public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The plugin's services with fake core services, a stub model server and a temporary data folder.</summary>
public sealed class Harness : IAsyncDisposable
{
    public required string Folder { get; init; }
    public required StubLlmServer Server { get; init; }
    public required ServiceProvider Services { get; init; }
    public DispatchLog Dispatched => Services.GetRequiredService<DispatchLog>();
    public FakeAuditLog Audit => (FakeAuditLog)Services.GetRequiredService<IQuasarAuditLog>();
    public SettingsStore Settings => Services.GetRequiredService<SettingsStore>();
    public AssistantRunner Runner => Services.GetRequiredService<AssistantRunner>();
    public ConversationStore Conversations => Services.GetRequiredService<ConversationStore>();

    public static async Task<Harness> StartAsync(
        IEnumerable<QuasarOperation> operations,
        TimeProvider? time = null
    )
    {
        var server = await StubLlmServer.StartAsync();
        var services = new ServiceCollection();
        var folder = Path.Combine(
            Path.GetTempPath(),
            "quasar-assistant-tests",
            Guid.NewGuid().ToString("N")
        );
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        if (time is not null)
            services.AddSingleton(time);
        foreach (var operation in operations)
            services.AddSingleton(operation);
        services.AddSingleton<DispatchLog>();
        services.AddScoped<IQuasarOperationDispatcher, FakeDispatcher>();
        services.AddSingleton<IQuasarAgentPrincipals, FakePrincipals>();
        services.AddSingleton<IQuasarAuditLog, FakeAuditLog>();
        services.AddScoped<IQuasarAgentOperations, FakeAgentOperations>();
        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(
                QuasarPolicyNames.CanView,
                p => p.RequireRole("viewer", "editor", "admin")
            );
            options.AddPolicy(
                QuasarPolicyNames.CanControlServers,
                p => p.RequireRole("editor", "admin")
            );
            options.AddPolicy(
                QuasarPolicyNames.CanActAsPlayer,
                p => p.RequireAssertion(_ => false)
            );
        });
        services.AddQuasarAssistant(folder);
        return new Harness
        {
            Folder = folder,
            Server = server,
            Services = services.BuildServiceProvider(),
        };
    }

    /// <summary>Sets up backends of one kind on the stub server, in this order for the admin role.</summary>
    public void UseBackends(BackendKind kind, params string[] names) =>
        Configure(settings =>
            settings with
            {
                Backends = names.Select(name => Backend(name, kind)).ToList(),
                Roles = new()
                {
                    [AssistantRoles.Admin] = settings.Role(AssistantRoles.Admin) with
                    {
                        Backends = [.. names],
                    },
                },
            }
        );

    public BackendSettings Backend(string name, BackendKind kind) =>
        new()
        {
            Name = name,
            Kind = kind,
            BaseUrl = Server.BaseUrl(name),
            Model = "model-" + name,
            FirstByteTimeout = 5,
            StallTimeout = 5,
        };

    public void Configure(Func<AssistantSettings, AssistantSettings> change) =>
        Settings.Save(change(Settings.Current));

    public void ConfigureRole(Func<RoleSettings, RoleSettings> change) =>
        Configure(settings =>
            settings with
            {
                Roles = new()
                {
                    [AssistantRoles.Admin] = change(settings.Role(AssistantRoles.Admin)),
                },
            }
        );

    public static ClaimsPrincipal Person(
        string name = "Ada",
        string subject = "steam:1",
        string role = "admin"
    ) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, subject),
                    new Claim(ClaimTypes.Name, name),
                    new Claim(ClaimTypes.Role, role),
                ],
                "test"
            )
        );

    /// <summary>Asks in a new conversation and returns it when the run is done.</summary>
    public async Task<Conversation> AskAsync(
        string text,
        ClaimsPrincipal? asker = null,
        PageContext? page = null
    )
    {
        asker ??= Person();
        var conversation = Conversations.Create(
            asker.FindFirstValue(ClaimTypes.NameIdentifier)!,
            asker.Identity!.Name!
        );
        await Runner.AskAsync(
            conversation,
            asker,
            text,
            page ?? new PageContext("http://quasar/servers/alpha", "alpha")
        );
        return conversation;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await Server.DisposeAsync();
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
    }
}
