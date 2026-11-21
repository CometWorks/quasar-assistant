using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Lore;
using QuasarAssistant.Skills;

namespace QuasarAssistant.Settings;

public sealed record SettingsReadRequest;

/// <summary>The settings as the API shows them: secrets only as set or not.</summary>
/// <param name="ApiKeys">The backends that have an API key.</param>
public sealed record SettingsView(
    AssistantSettings Settings,
    IReadOnlyList<string> ApiKeys,
    bool SandboxTokenSet
);

/// <summary>Replaces the backends. <paramref name="ApiKeys"/> sets a backend's key, an empty one removes it, a
/// backend left out keeps its key; a renamed backend is a new one, so give its key again.</summary>
public sealed record BackendsSetRequest(
    List<BackendSettings> Backends,
    Dictionary<string, string>? ApiKeys = null
);

/// <summary>Replaces the parts given of a role's settings; null keeps a part. <paramref name="Player"/> is for the
/// player role only, <paramref name="Monitor"/> for the monitor only.</summary>
public sealed record RoleSetRequest(
    string Role,
    RoleSettings? Settings = null,
    PlayerSupportSettings? Player = null,
    MonitorSettings? Monitor = null
);

public sealed record SkillsListRequest(string? Role = null);

/// <summary>A role's skills: the ones written on the settings page by name, and its sources.</summary>
public sealed record RoleSkillsView(
    string Role,
    IReadOnlyList<string> Written,
    IReadOnlyList<SkillSourceView> Sources
);

public sealed record SkillSourceRequest(string Role, string Name);

/// <summary>The body of <c>PUT /api/v1/assistant/skills/{role}/{name}</c>.</summary>
public sealed record SkillSourceBody(
    string Kind,
    string? Repository = null,
    string? Ref = null,
    List<string>? Paths = null,
    string? Url = null,
    string? Token = null,
    string? Content = null,
    string? FileName = null
);

/// <summary>The body of <c>PUT /api/v1/assistant/settings/roles/{role}</c>.</summary>
public sealed record RoleBody(
    RoleSettings? Settings = null,
    PlayerSupportSettings? Player = null,
    MonitorSettings? Monitor = null
);

/// <summary>
/// The settings and skill source operations and their REST API, for deployments that set the assistant up from a
/// script. They need <c>CanManageSecurity</c> like the settings page, are audited by core with secrets redacted,
/// and are on the agents' deny list.
/// </summary>
public static class SettingsApi
{
    public const string Read = "settings.read";
    public const string SetBackends = "settings.backends.set";
    public const string SetRole = "settings.role.set";
    public const string ListSkills = "skills.list";
    public const string SetSource = "skills.source.set";
    public const string RemoveSource = "skills.source.remove";
    public const string RefreshSource = "skills.source.refresh";
    public const string Route = "/api/v1/assistant";

    private static readonly string[] Policies = [QuasarPolicyNames.CanManageSecurity];
    private static readonly TimeSpan FetchDeadline = GitFetcher.Timeout + TimeSpan.FromSeconds(30);

    public static IServiceCollection AddSettingsOperations(this IServiceCollection services)
    {
        var plugin = AssistantServiceCollectionExtensions.PluginId;
        services.AddQuasarOperation<SettingsReadRequest, SettingsView>(
            plugin,
            Read,
            Policies,
            (context, _, _) =>
                Task.FromResult(View(context.Services.GetRequiredService<SettingsStore>())),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                AgentAccess = QuasarAgentAccess.Denied,
                Description = "Reads the assistant's settings, without secrets.",
            }
        );
        services.AddQuasarOperation<BackendsSetRequest, SettingsView>(
            plugin,
            SetBackends,
            Policies,
            (context, request, _) =>
                Invalid(() =>
                    SetBackendsOf(context.Services.GetRequiredService<SettingsStore>(), request)
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Description = "Replaces the assistant's model backends and sets their API keys.",
            }
        );
        services.AddQuasarOperation<RoleSetRequest, SettingsView>(
            plugin,
            SetRole,
            Policies,
            (context, request, _) =>
                Invalid(() =>
                    SetRoleOf(context.Services.GetRequiredService<SettingsStore>(), request)
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Description =
                    "Sets an assistant role's settings: backends, modes, MCP servers and limits, and the player support or monitor settings.",
            }
        );
        services.AddQuasarOperation<SkillsListRequest, IReadOnlyList<RoleSkillsView>>(
            plugin,
            ListSkills,
            Policies,
            (context, request, _) =>
                Invalid(() =>
                {
                    var paths = context.Services.GetRequiredService<AssistantPaths>();
                    var sources = context.Services.GetRequiredService<SkillSourceStore>();
                    var roles = request.Role is null
                        ? AssistantRoles.All
                        : [SkillSourceStore.CheckRole(request.Role)];
                    return Task.FromResult<IReadOnlyList<RoleSkillsView>>(
                        roles
                            .Select(role => new RoleSkillsView(
                                role,
                                // The monitor reads the admin assistant's written skills.
                                role == AssistantRoles.Monitor
                                    ? []
                                    : SkillLibrary
                                        .InlineNames(paths, role)
                                        .Order(StringComparer.Ordinal)
                                        .ToList(),
                                sources.List(role)
                            ))
                            .ToList()
                    );
                }),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                AgentAccess = QuasarAgentAccess.Denied,
                Description = "Lists each assistant role's skills and skill sources.",
            }
        );
        services.AddQuasarOperation<SkillSourceSetRequest, SkillSourceView>(
            plugin,
            SetSource,
            Policies,
            (context, request, token) =>
                Invalid(() =>
                    context.Services.GetRequiredService<SkillSourceStore>().SetAsync(request, token)
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Deadline = FetchDeadline,
                Description =
                    "Adds or replaces a skill source of an assistant role: Git repository folders, a ZIP or a SKILL.md.",
            }
        );
        services.AddQuasarOperation<SkillSourceRequest, object>(
            plugin,
            RemoveSource,
            Policies,
            (context, request, _) =>
                Invalid(() =>
                    context
                        .Services.GetRequiredService<SkillSourceStore>()
                        .Remove(request.Role, request.Name)
                        ? Task.FromResult<object>(new { removed = true })
                        : throw new QuasarOperationException(
                            404,
                            "That role has no skill source of that name."
                        )
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Description = "Removes a skill source of an assistant role.",
            }
        );
        services.AddQuasarOperation<SkillSourceRequest, SkillSourceView>(
            plugin,
            RefreshSource,
            Policies,
            (context, request, token) =>
                Invalid(async () =>
                    await context
                        .Services.GetRequiredService<SkillSourceStore>()
                        .RefreshAsync(request.Role, request.Name, token)
                    ?? throw new QuasarOperationException(
                        404,
                        "That role has no skill source of that name."
                    )
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Reversal = QuasarReversal.NothingToReverse,
                Deadline = FetchDeadline,
                Description = "Fetches a Git skill source of an assistant role again.",
            }
        );
        return services;
    }

    /// <summary>JSON routes for scripts, with a bearer token or a session; each one runs its operation through
    /// core's dispatcher.</summary>
    public static void MapSettingsApi(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup(Route).RequireAuthorization();
        // A cast: as a plain RequestDelegate the result would be dropped.
        routes.MapGet(
            "/settings",
            (Delegate)((HttpContext http) => Dispatch(http, Read, new SettingsReadRequest()))
        );
        routes.MapPut(
            "/settings/backends",
            (BackendsSetRequest body, HttpContext http) => Dispatch(http, SetBackends, body)
        );
        routes.MapPut(
            "/settings/roles/{role}",
            (string role, RoleBody body, HttpContext http) =>
                Dispatch(
                    http,
                    SetRole,
                    new RoleSetRequest(role, body.Settings, body.Player, body.Monitor)
                )
        );
        routes.MapGet(
            "/skills",
            (string? role, HttpContext http) =>
                Dispatch(http, ListSkills, new SkillsListRequest(role))
        );
        routes.MapPut(
            "/skills/{role}/{name}",
            (string role, string name, SkillSourceBody body, HttpContext http) =>
                Dispatch(
                    http,
                    SetSource,
                    new SkillSourceSetRequest(
                        role,
                        name,
                        body.Kind,
                        body.Repository,
                        body.Ref,
                        body.Paths,
                        body.Url,
                        body.Token,
                        body.Content,
                        body.FileName
                    )
                )
        );
        routes.MapDelete(
            "/skills/{role}/{name}",
            (string role, string name, HttpContext http) =>
                Dispatch(http, RemoveSource, new SkillSourceRequest(role, name))
        );
        routes.MapPost(
            "/skills/{role}/{name}/refresh",
            (string role, string name, HttpContext http) =>
                Dispatch(http, RefreshSource, new SkillSourceRequest(role, name))
        );
    }

    public static SettingsView View(SettingsStore store)
    {
        var settings = store.Current;
        return new(
            settings,
            settings
                .Backends.Where(backend => store.HasApiKey(backend.Name))
                .Select(backend => backend.Name)
                .ToList(),
            store.HasSandboxToken
        );
    }

    internal static Task<SettingsView> SetBackendsOf(
        SettingsStore store,
        BackendsSetRequest request
    )
    {
        var backends =
            request.Backends ?? throw new ArgumentException("Give the backends as a list.");
        foreach (var backend in backends)
            if (BackendProblem(backend) is { } problem)
                throw new ArgumentException(problem);
        if (
            backends
                .GroupBy(backend => backend.Name.Trim())
                .FirstOrDefault(group => group.Count() > 1) is
            { } twice
        )
            throw new ArgumentException($"Two backends are named {twice.Key}.");
        var keys = request.ApiKeys ?? [];
        if (
            keys.Keys.FirstOrDefault(name =>
                backends.All(backend => backend.Name.Trim() != name)
            ) is
            { } unknown
        )
            throw new ArgumentException($"There is no backend named {unknown} for the API key.");
        store.Save(store.Current with { Backends = backends.Select(Normalized).ToList() });
        foreach (var (name, key) in keys)
            store.SetApiKey(name, string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        return Task.FromResult(View(store));
    }

    internal static Task<SettingsView> SetRoleOf(SettingsStore store, RoleSetRequest request)
    {
        var role = SkillSourceStore.CheckRole(request.Role);
        if (request.Player is not null && role != AssistantRoles.Player)
            throw new ArgumentException("Player support settings go with the player role.");
        if (request.Monitor is not null && role != AssistantRoles.Monitor)
            throw new ArgumentException("Monitor settings go with the monitor role.");
        var settings = store.Current;
        if (request.Settings is { } given)
        {
            var form = RoleForm.From(given, role);
            if (form.Validate() is { } problem)
                throw new ArgumentException(problem);
            var names = settings.Backends.Select(backend => backend.Name).ToHashSet();
            if (given.Backends.FirstOrDefault(name => !names.Contains(name)) is { } unknown)
                throw new ArgumentException($"There is no backend named {unknown}.");
            var normalized = form.ToSettings();
            settings = settings.WithRole(
                role,
                normalized with
                {
                    CodeExecution = given.CodeExecution,
                }
            );
        }
        if (request.Player is { } player)
            settings = settings with { Player = PlayerForm.From(player).ToSettings() };
        if (request.Monitor is { } monitor)
            settings = settings with { Monitor = MonitorForm.From(monitor).ToSettings() };
        store.Save(settings);
        return Task.FromResult(View(store));
    }

    private static string? BackendProblem(BackendSettings backend)
    {
        if (string.IsNullOrWhiteSpace(backend.Name) || backend.Name.Trim().Length > 64)
            return "A backend needs a name of at most 64 characters.";
        if (
            !Uri.TryCreate(backend.BaseUrl?.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
        )
            return $"The base URL of {backend.Name} must be an http or https URL.";
        if (string.IsNullOrWhiteSpace(backend.Model))
            return $"The backend {backend.Name} needs a model.";
        return null;
    }

    private static BackendSettings Normalized(BackendSettings backend) =>
        backend with
        {
            Name = backend.Name.Trim(),
            BaseUrl = backend.BaseUrl.Trim(),
            Model = backend.Model.Trim(),
            Parallel = Math.Max(1, backend.Parallel),
            ContextWindow = Math.Max(1024, backend.ContextWindow),
            MaxTokens = Math.Max(16, backend.MaxTokens),
            FirstByteTimeout = backend.FirstByteTimeout is > 0 ? backend.FirstByteTimeout : null,
            StallTimeout = backend.StallTimeout is > 0 ? backend.StallTimeout : null,
            Cooldown = Math.Max(0, backend.Cooldown),
            MaxCooldown = Math.Max(Math.Max(0, backend.Cooldown), backend.MaxCooldown),
            Headers = backend.Headers ?? [],
        };

    private static async Task<T> Invalid<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (ArgumentException error)
        {
            throw new QuasarOperationException(400, error.Message);
        }
    }

    private static async Task<IResult> Dispatch(HttpContext http, string operation, object request)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        var dispatcher = http.RequestServices.GetRequiredService<IQuasarOperationDispatcher>();
        var result = await dispatcher.DispatchAsync(
            new QuasarOperationContext(http.User, http.RequestServices),
            AssistantServiceCollectionExtensions.PluginId,
            operation,
            JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web),
            http.RequestAborted
        );
        return result.ToHttpResult();
    }
}
