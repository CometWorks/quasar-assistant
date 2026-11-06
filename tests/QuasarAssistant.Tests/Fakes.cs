using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Discord;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Plugin.Abstractions.Servers;
using QuasarAssistant.Agent;
using QuasarAssistant.GridBackups;
using QuasarAssistant.Llm;
using QuasarAssistant.Players;
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
        // As core: only the player agent satisfies CanActAsPlayer, and it holds no role for anything else.
        var isPlayer =
            context.User.IsQuasarAgent(out var agent) && agent.Kind == QuasarAgentKind.Player;
        if (ToolCatalog.IsPlayerOperation(operation) != isPlayer)
            return new(403, Error: "The caller cannot access this SDK operation.");
        if (log.Refuse.TryGetValue(key, out var reason))
            return new(403, Error: reason);
        try
        {
            return new(200, await operation.InvokeAsync(context, request, cancellationToken));
        }
        catch (QuasarOperationException error)
        {
            return new(error.StatusCode, Error: error.Message);
        }
    }
}

/// <summary>Builds a real assistant identity, as core would, with the asker's name as a claim for the tests.</summary>
public sealed class FakePrincipals : IQuasarAgentPrincipals
{
    public Task<ClaimsPrincipal> CreateAssistantAsync(
        ClaimsPrincipal asker,
        string conversationId,
        string request
    )
    {
        var identity = PlayerAsk.Identity(
            QuasarAgentKind.Assistant,
            asker,
            null,
            null,
            conversationId
        );
        identity.AddClaim(new Claim("asker", asker.Identity!.Name!));
        return Task.FromResult(new ClaimsPrincipal(identity));
    }
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

    /// <summary>The log core would query, newest last.</summary>
    public List<QuasarAuditEntry> Entries { get; } = [];

    public Task<IReadOnlyList<QuasarAuditEntry>> QueryAsync(
        QuasarAuditFilter filter,
        CancellationToken token
    ) =>
        Task.FromResult<IReadOnlyList<QuasarAuditEntry>>(
            Entries
                .Where(entry =>
                    (filter.From is null || entry.Time >= filter.From)
                    && (filter.Result is null || entry.Result == filter.Result)
                    && (
                        filter.Operation is null
                        || entry.Operation.Contains(
                            filter.Operation,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                .Reverse()
                .Take(filter.Take)
                .ToList()
        );

    /// <summary>A restore the player got, as core logs it.</summary>
    public QuasarAuditEntry AddRestore(
        string steamId,
        DateTimeOffset time,
        QuasarAuditMode mode = QuasarAuditMode.Applied
    )
    {
        var entry = new QuasarAuditEntry
        {
            Id = "restore" + Interlocked.Increment(ref _next),
            Time = time,
            Principal = new(QuasarAuditPrincipalKind.PlayerAgent, "agent:player", "Player agent"),
            Requester = new(SteamId: steamId),
            Operation = ToolCatalog.RestoreKey,
            Area = "my",
            Mode = mode,
            Result = QuasarAuditResult.Ok,
        };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>An admin's undo of an entry.</summary>
    public void AddUndo(QuasarAuditEntry entry, DateTimeOffset time) =>
        Entries.Add(
            entry with
            {
                Id = "undo" + Interlocked.Increment(ref _next),
                Time = time,
                Principal = new(QuasarAuditPrincipalKind.Person, "steam:9", "Admin"),
                Requester = new(Subject: "steam:9"),
                Reverses = entry.Id,
            }
        );
}

/// <summary>Core's Discord channels: records the posts.</summary>
public sealed class FakeDiscord : IQuasarDiscordChannels
{
    public ConcurrentQueue<(string Channel, string Text)> Posts { get; } = new();

    public Task<bool> PostAsync(string channelId, string text, CancellationToken token)
    {
        Posts.Enqueue((channelId, text));
        return Task.FromResult(true);
    }
}

/// <summary>A player request as core makes it, with the replies it got.</summary>
public sealed class PlayerAsk
{
    public List<string> Replies { get; } = [];

    public static ClaimsPrincipal Agent(
        string steamId = "76561198000000001",
        string name = "Pilot",
        string? server = "alpha"
    ) => new(Identity(QuasarAgentKind.Player, null, steamId, name, "steam:" + steamId, server));

    /// <summary>Only core builds agent identities; the tests reach its internal constructor.</summary>
    public static QuasarAgentIdentity Identity(
        QuasarAgentKind kind,
        ClaimsPrincipal? asker,
        string? steamId,
        string? name,
        string conversationId,
        string? server = "alpha"
    ) =>
        (QuasarAgentIdentity)
            Activator.CreateInstance(
                typeof(QuasarAgentIdentity),
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [
                    kind,
                    asker,
                    steamId,
                    name,
                    null,
                    conversationId,
                    "!ask",
                    kind == QuasarAgentKind.Assistant ? new[] { "editor" } : Array.Empty<string>(),
                    kind == QuasarAgentKind.Player ? server : null,
                ],
                null
            )!;

    public QuasarChatCommandRequest Game(string text, string steamId = "76561198000000001") =>
        new(
            "ask",
            Agent(steamId),
            "steam:" + steamId,
            text,
            new QuasarChatCommandSource(QuasarChatChannel.Game, "alpha"),
            Reply
        );

    /// <summary>A Discord user without a link: no player principal.</summary>
    public QuasarChatCommandRequest Unlinked(string text, string discordId = "555") =>
        new(
            "ask",
            null,
            "discord:" + discordId,
            text,
            new QuasarChatCommandSource(QuasarChatChannel.Discord, DiscordUserId: discordId),
            Reply
        );

    private Task Reply(string text, CancellationToken token)
    {
        lock (Replies)
            Replies.Add(text);
        return Task.CompletedTask;
    }
}

/// <summary>Core's server queries over servers, players and grids a test sets up.</summary>
public sealed class FakeServers : IQuasarServerQueries
{
    public List<QuasarServerTarget> Servers { get; } = [];

    /// <summary>The agent ID of the node each player is on, by Steam ID.</summary>
    public ConcurrentDictionary<string, string> Players { get; } = new();

    /// <summary>The agent ID of the node that has each grid, by grid ID.</summary>
    public ConcurrentDictionary<long, string> Grids { get; } = new();

    /// <summary>The agent ID of the node owning the area around a position, or null.</summary>
    public Func<(double X, double Y, double Z), string?> PositionOwner { get; set; } = _ => null;
    public ConcurrentQueue<(double X, double Y, double Z, double Radius)> PositionQueries { get; } =
        new();

    /// <returns>The server's agent ID.</returns>
    public string Standalone(string name, bool gridBackups = true)
    {
        var agentId = name + ":1";
        Servers.Add(
            new(name, name, false, [new(agentId, null, gridBackups ? [GridBackupsPluginId] : [])])
        );
        return agentId;
    }

    /// <returns>The nodes' agent IDs, <c>name-node:1</c>.</returns>
    public string[] Cluster(string name, params string[] nodes)
    {
        var agentIds = nodes.Select(node => $"{name}-{node}:1").ToArray();
        Servers.Add(
            new(
                name,
                name,
                true,
                nodes
                    .Select(
                        (node, i) =>
                            new QuasarServerNode(agentIds[i], "node-" + node, [GridBackupsPluginId])
                    )
                    .ToList()
            )
        );
        return agentIds;
    }

    // Plugin IDs match ignoring case.
    private static readonly string GridBackupsPluginId =
        GridBackupsTools.GridBackupsPluginId.ToLowerInvariant();

    public IReadOnlyList<QuasarServerTarget> GetServers() => Servers;

    public string? FindPlayer(string server, string steamId) =>
        Players.TryGetValue(steamId, out var agentId)
        && Servers.Any(target =>
            target.UniqueName == server && target.Nodes.Any(node => node.AgentId == agentId)
        )
            ? agentId
            : null;

    public Task<IReadOnlyDictionary<long, string>> FindGridsAsync(
        string server,
        IEnumerable<long> gridIds,
        CancellationToken token
    ) =>
        Task.FromResult<IReadOnlyDictionary<long, string>>(
            gridIds.Distinct().Where(Grids.ContainsKey).ToDictionary(id => id, id => Grids[id])
        );

    public Task<string?> FindPositionOwnerAsync(
        string server,
        double x,
        double y,
        double z,
        double radiusMeters,
        CancellationToken token,
        string? pluginId = null
    )
    {
        Assert.Equal(GridBackupsTools.GridBackupsPluginId, pluginId);
        PositionQueries.Enqueue((x, y, z, radiusMeters));
        return Task.FromResult(PositionOwner((x, y, z)));
    }
}

/// <summary>The companion channel: records each request and answers from <see cref="Answer"/>.</summary>
public sealed class FakeCompanion : IQuasarCompanionChannel
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public ConcurrentQueue<(
        string AgentId,
        string PluginId,
        string Operation,
        JsonElement Request,
        TimeSpan? Timeout
    )> Calls { get; } = new();

    /// <summary>The answer by agent ID, operation and request.</summary>
    public Func<string, string, JsonElement, object> Answer { get; set; } =
        (_, _, _) => new { ok = true };

    public Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        CancellationToken cancellationToken
    ) => Send<TRequest, TResponse>(serverId, companionPluginId, operation, request, null);

    public Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) => Send<TRequest, TResponse>(serverId, companionPluginId, operation, request, timeout);

    private Task<TResponse> Send<TRequest, TResponse>(
        string serverId,
        string pluginId,
        string operation,
        TRequest request,
        TimeSpan? timeout
    )
    {
        var json = JsonSerializer.SerializeToElement(request, Web);
        Calls.Enqueue((serverId, pluginId, operation, json, timeout));
        var answer = JsonSerializer.SerializeToElement(Answer(serverId, operation, json), Web);
        return Task.FromResult(answer.Deserialize<TResponse>(Web)!);
    }
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
    public FakeDiscord Discord => Services.GetRequiredService<FakeDiscord>();
    public AskCommand Players => Services.GetRequiredService<AskCommand>();

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
        services.AddSingleton<FakeDiscord>();
        services.AddSingleton<IQuasarDiscordChannels>(provider =>
            provider.GetRequiredService<FakeDiscord>()
        );
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
                QuasarPolicyNames.CanUseAssistant,
                p => p.RequireRole("editor", "admin")
            );
            options.AddPolicy(
                QuasarPolicyNames.CanActAsPlayer,
                p => p.RequireAssertion(_ => false)
            );
        });
        services.AddSingleton<FakeServers>();
        services.AddSingleton<IQuasarServerQueries>(p => p.GetRequiredService<FakeServers>());
        services.AddSingleton<FakeCompanion>();
        services.AddSingleton<IQuasarCompanionChannel>(p => p.GetRequiredService<FakeCompanion>());
        services.AddQuasarAssistant(folder);
        // A test operation stands in for the plugin's own one of the same key.
        var replaced = operations.ToDictionary(op => op.Key);
        foreach (var descriptor in services.ToList())
            if (
                descriptor.ImplementationInstance is QuasarOperation own
                && replaced.TryGetValue(own.Key, out var test)
                && test != own
            )
                services.Remove(descriptor);
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
            settings.WithRole(
                AssistantRoles.Admin,
                settings.Role(AssistantRoles.Admin) with
                {
                    Backends = [.. names],
                }
            ) with
            {
                Backends = names.Select(name => Backend(name, kind)).ToList(),
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

    /// <summary>Turns the player agent on with backends of one kind on the stub server.</summary>
    public void UsePlayerBackends(BackendKind kind, params string[] names) =>
        Configure(settings =>
            settings.WithRole(
                AssistantRoles.Player,
                settings.Role(AssistantRoles.Player) with
                {
                    Backends = [.. names],
                }
            ) with
            {
                Backends =
                [
                    .. settings.Backends.Where(b => !names.Contains(b.Name)),
                    .. names.Select(name => Backend(name, kind)),
                ],
                Player = settings.Player with { Enabled = true },
            }
        );

    public void ConfigurePlayer(Func<RoleSettings, RoleSettings> change) =>
        Configure(settings =>
            settings.WithRole(AssistantRoles.Player, change(settings.Role(AssistantRoles.Player)))
        );

    public void ConfigureRole(Func<RoleSettings, RoleSettings> change) =>
        Configure(settings =>
            settings.WithRole(AssistantRoles.Admin, change(settings.Role(AssistantRoles.Admin)))
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
