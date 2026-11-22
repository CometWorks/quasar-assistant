using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Lore;
using QuasarAssistant.Mcp;
using QuasarAssistant.Monitoring;
using QuasarAssistant.Players;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;
using static QuasarAssistant.Panel.PanelOperations;

namespace QuasarAssistant.Panel;

internal static class SettingsPanelOperations
{
    internal static void AddSettingsPanel(this IServiceCollection s)
    {
        s.AddMemoryCache();
        s.Operation<SettingsReadRequest, SettingsPanel>(
            "settings.panel",
            Configure,
            async (c, _, _) =>
            {
                var store = c.Get<SettingsStore>();
                var settings = store.Current;
                var mcp = c.Get<McpRegistry>();
                var catalog = await c.Get<ToolCatalog>().AgentOperationsAsync();
                List<ServerTools> servers = [];
                foreach (var role in AssistantRoles.All)
                foreach (
                    var name in settings
                        .EffectiveRole(role)
                        .McpServers.Select(server => server.Name)
                )
                {
                    var status = mcp.Status(role, name);
                    servers.Add(
                        new(
                            role,
                            name,
                            [.. store.McpHeaderNames(role, name)],
                            [
                                .. mcp.Tools(role)
                                    .Where(t => t.Server == name)
                                    .Select(t => new ToolOption(t.Name, t.Description)),
                            ],
                            status?.Time,
                            status?.Error
                        )
                    );
                }
                return new(
                    SettingsApi.PublicSettings(settings),
                    [.. settings.Backends.Where(b => store.HasApiKey(b.Name)).Select(b => b.Name)],
                    store.HasSandboxToken,
                    servers,
                    AssistantRoles.All.ToDictionary(
                        role => role,
                        role =>
                            ToolCatalog
                                .OfRole(catalog, role)
                                .Select(op => new ModeOption(
                                    op.Key,
                                    ToolCatalog.Area(op),
                                    ToolCatalog.Effect(op) == QuasarOperationEffect.Read
                                ))
                                .ToList()
                    ),
                    c.Get<MetricsLog>().Summarize(c.Get<TimeProvider>().GetUtcNow().AddDays(-14)),
                    c.Get<MonitorBudget>().Status(),
                    AskCommand.PrivacyNotice(
                        settings with
                        {
                            Player = settings.Player with { PrivacyNotice = "" },
                        }
                    ),
                    SkillLibrary.BookCommit
                );
            }
        );
        s.Operation<SettingsReadRequest, string>("settings.snapshot", Configure, (c, _, _) =>
        {
            var id = Guid.NewGuid().ToString("N");
            c.Get<IMemoryCache>()
                .Set(
                    (Plugin, c.Subject(), id),
                    Take(c),
                    new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromHours(2),
                        Size = 1,
                    }
                );
            return Task.FromResult(id);
        });
        s.Operation<ItemId, List<string>>("settings.changes", Configure, (c, r, _) =>
            Task.FromResult(Snapshot(c, r).Changes(Take(c)).ToList())
        );
        s.Operation<ItemId, bool>(
            "settings.revert",
            Configure,
            (c, r, _) =>
            {
                Snapshot(c, r)
                    .Restore(
                        c.Get<SettingsStore>(),
                        c.Get<SkillLibrary>(),
                        c.Get<SkillSourceStore>()
                    );
                c.Get<IMemoryCache>().Remove((Plugin, c.Subject(), r.Id));
                return Task.FromResult(true);
            },
            true
        );
        s.Operation<ItemId, TestResult>("settings.test", Configure, Test, true);
        s.Operation<SettingsReadRequest, bool>(
            "settings.mcp.refresh",
            Configure,
            async (c, _, t) =>
            {
                await c.Get<McpRegistry>().RefreshAsync(t);
                return true;
            },
            true
        );
        s.Operation<ItemId, List<WrittenSkill>>("skills.written", Configure, (c, r, _) =>
            Task.FromResult(
                c.Get<SkillLibrary>()
                    .Skills(SkillSourceStore.CheckRole(r.Id))
                    .Select(x => new WrittenSkill(x.Name, x.Text, x.Summary))
                    .ToList()
            )
        );
        s.Operation<ItemId, List<SkillSourceView>>("skills.sources", Configure, (c, r, _) =>
            Task.FromResult(
                c.Get<SkillSourceStore>().List(SkillSourceStore.CheckRole(r.Id)).ToList()
            )
        );
        // Data (including write-only credentials) travels in the staged body rather than the audit arguments.
        Upload<SettingsChange>(s, "settings.save", Configure, 4 * 1024 * 1024, (c, r, _) =>
        {
            Save(c.Get<SettingsStore>(), r);
            return Task.CompletedTask;
        });
        Upload<SkillEdit>(s, "skills.write", Configure, 2 * 1024 * 1024, (c, r, _) =>
        {
            var role = SkillSourceStore.CheckRole(r.Role);
            if (!SkillLibrary.IsValidName(r.Name) || r.Text?.Length > 200000)
                throw new ArgumentException("Invalid skill name or text length.");
            var skills = c.Get<SkillLibrary>();
            lock (skills)
            {
                if (r.Create && skills.Skills(role).Any(x => x.Name == r.Name))
                    throw new QuasarOperationException(409, "The skill already exists.");
                if (r.Text is null)
                    skills.Delete(role, r.Name);
                else
                    skills.Save(role, r.Name, r.Text);
            }
            return Task.CompletedTask;
        });
        s.AddQuasarUpload<SkillSourceSetRequest, bool>(
            Plugin,
            "skills.source.upload",
            [Configure],
            SkillTree.MaxZipBytes,
            async (caller, request, body, token) =>
            {
                if (request.Kind is not (SkillSourceKinds.Zip or SkillSourceKinds.File))
                    throw new ArgumentException("Choose a ZIP or SKILL.md file.");
                using var buffer = new MemoryStream();
                await body.CopyToAsync(buffer, token);
                var content = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
                return new(async commit =>
                {
                    await Dispatch(
                        caller,
                        SettingsApi.SetSource,
                        request with
                        {
                            Content = content,
                        },
                        commit
                    );
                    return true;
                });
            },
            Metadata(true)
        );
        Upload<LoreSetRequest>(s, "lore.save", Edit, 2 * 1024 * 1024, (c, r, t) =>
            Dispatch(c, LoreOperations.Set, r, t)
        );
    }

    private static async Task Dispatch<T>(
        QuasarOperationContext c,
        string operation,
        T request,
        CancellationToken token
    )
    {
        var result = await c.Get<IQuasarOperationDispatcher>()
            .DispatchAsync(
                new QuasarOperationContext(c.User, c.Services),
                Plugin,
                operation,
                JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web),
                token
            );
        if (result.Error is not null)
            throw new QuasarOperationException(result.StatusCode, result.Error);
    }

    private static ConfigSnapshot Take(QuasarOperationContext c) =>
        ConfigSnapshot.Take(
            c.Get<SettingsStore>(),
            c.Get<SkillLibrary>(),
            c.Get<SkillSourceStore>()
        );

    private static ConfigSnapshot Snapshot(QuasarOperationContext c, ItemId request) =>
        c.Get<IMemoryCache>().Get<ConfigSnapshot>((Plugin, c.Subject(), request.Id))
        ?? throw new QuasarOperationException(
            404,
            "The settings session expired. Reopen the page."
        );

    private static void Upload<T>(
        IServiceCollection services,
        string name,
        string policy,
        long limit,
        Func<QuasarOperationContext, T, CancellationToken, Task> commit
    ) => services.Upload<T, bool>(name, policy, limit, async (caller, request, token) =>
        {
            await commit(caller, request, token);
            return true;
        });

    internal static void Save(SettingsStore store, SettingsChange r)
    {
        store.Edit(() =>
        {
            var current = store.Current;
            if (
                JsonSerializer.Serialize(SettingsApi.PublicSettings(current))
                != JsonSerializer.Serialize(r.Before)
            )
                throw new QuasarOperationException(
                    409,
                    "Settings changed elsewhere. Reload before saving."
                );
            var settings = r.After;
            foreach (var role in AssistantRoles.All)
                if (RoleForm.From(settings.Role(role), role).Validate() is { } error)
                    throw new ArgumentException(error);
            if (
                settings.ResultCapBytes is < 1024 or > 1048576
                || settings.ConversationRetentionDays < 1
                || settings.ProposalExpiryDays < 1
                || settings.Sandbox.Url.Length > 0
                    && (
                        !Uri.TryCreate(settings.Sandbox.Url, UriKind.Absolute, out var uri)
                        || uri.Scheme is not ("http" or "https")
                    )
            )
                throw new ArgumentException("Invalid assistant limits or sandbox URL.");
            if (settings.Backends.Select(b => b.Name).Distinct().Count() != settings.Backends.Count)
                throw new ArgumentException("Backend names must be unique.");
            var backends = new List<BackendSettings>();
            foreach (var backend in settings.Backends)
            {
                if (SettingsApi.BackendProblem(backend) is { } error)
                    throw new ArgumentException(error);
                var original = backend.Name == r.BackendName ? r.OriginalBackend : backend.Name;
                var headers = current.Backends.FirstOrDefault(b => b.Name == original)?.Headers;
                backends.Add(
                    backend.Name == r.BackendName && r.ReplaceBackendHeaders
                        ? backend
                        : backend with
                        {
                            Headers = headers ?? [],
                        }
                );
            }
            foreach (var h in r.HeaderChanges)
            {
                var role = SkillSourceStore.CheckRole(h.Role);
                if (
                    !settings.Role(role).McpServers.Any(server => server.Name == h.Name)
                    || h.Original is not null
                        && !current.Role(role).McpServers.Any(server => server.Name == h.Original)
                )
                    throw new ArgumentException(
                        "Headers must belong to a configured MCP server of this role."
                    );
            }
            if (r.BackendName is not null && !backends.Any(b => b.Name == r.BackendName))
                throw new ArgumentException("Unknown backend for the credential edit.");
            var edits = r
                .HeaderChanges.Select(h =>
                    (Edit: h, Values: h.Headers ?? store.McpHeaders(h.Role, h.Original ?? h.Name))
                )
                .ToList();
            if (r.OriginalBackend is not null && r.BackendName is not null)
                store.RenameApiKey(r.OriginalBackend, r.BackendName);
            if (r.SandboxToken is not null)
                store.SetSandboxToken(r.SandboxToken);
            if (r.BackendName is not null && r.ApiKey is not null)
                store.SetApiKey(r.BackendName, r.ApiKey);
            foreach (var (edit, values) in edits)
                store.SetMcpHeaders(edit.Role, edit.Name, values);
            // Publish the settings only after discovery can use the updated credentials.
            store.Save(settings with { Backends = backends });
        });
    }

    private static async Task<TestResult> Test(
        QuasarOperationContext c,
        ItemId r,
        CancellationToken token
    )
    {
        var store = c.Get<SettingsStore>();
        var backend = store.Current.Backends.FirstOrDefault(b => b.Name == r.Id)
            ?? throw new QuasarOperationException(404, "Backend not found.");
        var clock = Stopwatch.StartNew();
        TimeSpan? firstText = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var answer = await c.Get<LlmClient>()
                .SendAsync(
                    backend with
                    {
                        MaxTokens = 20,
                        Thinking = ThinkingLevel.None,
                    },
                    store.ApiKey(backend.Name),
                    new ChatRequest(
                        "",
                        [],
                        [new ChatMessage { Role = ChatRole.User, Text = "1+1=" }]
                    ),
                    text => firstText ??= text.Length > 0 ? clock.Elapsed : null,
                    timeout.Token
                );
            var ok =
                answer.Text.Contains('2')
                || answer.Text.Contains("two", StringComparison.OrdinalIgnoreCase);
            var first = firstText is { } at ? $", first text after {at.TotalSeconds:0.0} s" : "";
            var text = answer.Text.Length > 120 ? answer.Text[..120] + "…" : answer.Text;
            return new(
                ok,
                $"{(ok ? "OK" : "Unexpected answer")} in {clock.Elapsed.TotalSeconds:0.0} s{first}: {text}"
            );
        }
        catch (Exception error)
            when (error is BackendFailure or BackendRejected or OperationCanceledException)
        {
            return new(false, $"Failed after {clock.Elapsed.TotalSeconds:0.0} s: {error.Message}");
        }
    }
}
