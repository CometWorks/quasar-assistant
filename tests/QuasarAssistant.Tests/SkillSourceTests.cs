using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Lore;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;

namespace QuasarAssistant.Tests;

public sealed class SkillSourceTests
{
    private const string World = """
        ---
        name: octahedron-world
        description: Places, factions and resources of the Octahedron world.
        ---
        # Octahedron world

        See places.md.
        """;

    private static async Task<(
        Harness Harness,
        SkillSourceStore Sources,
        SkillLibrary Skills
    )> StartAsync()
    {
        var harness = await Harness.StartAsync([]);
        var sources = harness.Services.GetRequiredService<SkillSourceStore>();
        sources.AllowFileUrls = true;
        return (harness, sources, harness.Services.GetRequiredService<SkillLibrary>());
    }

    [Fact]
    public void ZipTreeGivesEverySkillWithItsTextSupportFiles()
    {
        var zip = Zip(
            ("pack/skills/world/SKILL.md", World),
            ("pack/skills/world/places.md", "# Places\n"),
            ("pack/skills/world/data/ores.json", "{\"ore\": \"platinum\"}"),
            ("pack/skills/world/logo.png", "\0PNG"),
            ("pack/skills/world/nested/SKILL.md", "# Nested\n"),
            ("pack/skills/world/nested/more.md", "# More\n"),
            ("pack/skills/Player Support/SKILL.md", "# Support\n\nRules.\n"),
            ("__MACOSX/pack/skills/world/._SKILL.md", "junk"),
            ("pack/.git/config", "x"),
            ("pack/README.md", "Not a skill file.\n")
        );

        var skills = SkillTree.Find(SkillTree.ReadZip(zip), "fallback");

        Assert.Equal(
            ["Player-Support", "nested", "octahedron-world"],
            skills.Select(skill => skill.Name).Order(StringComparer.Ordinal)
        );
        var world = skills.Single(skill => skill.Name == "octahedron-world");
        Assert.Equal("pack/skills/world", world.Folder);
        Assert.Equal("Places, factions and resources of the Octahedron world.", world.Description);
        // The binary logo is left out, the nested skill keeps its own files.
        Assert.Equal(
            ["SKILL.md", "data/ores.json", "places.md"],
            world.Files.Select(file => file.Path)
        );
        Assert.Equal(
            ["SKILL.md", "more.md"],
            skills.Single(skill => skill.Name == "nested").Files.Select(file => file.Path)
        );
    }

    [Theory]
    [InlineData("# Just text\n", "faq")]
    [InlineData("---\nname: rules\ndescription: Server rules\n---\n# Rules\n", "rules")]
    [InlineData("---\nname: \"not valid!\"\n---\n# Rules\n", "faq")]
    public async Task SingleSkillFileIsNamedByItsFrontMatterOrTheSource(string text, string name)
    {
        var (harness, sources, skills) = await StartAsync();
        await using var _h = harness;

        var view = await sources.SetAsync(
            new(
                AssistantRoles.Player,
                "faq",
                SkillSourceKinds.File,
                Content: Base64(text),
                FileName: "SKILL.md"
            ),
            default
        );

        Assert.Null(view.Error);
        Assert.Equal(SkillTree.Sha256(Encoding.UTF8.GetBytes(text)), view.Revision);
        Assert.Equal([name], view.Skills.Select(skill => skill.Name));
        Assert.Equal(text, skills.Read(AssistantRoles.Player, name, 0));
        Assert.Contains($"- {name}: ", skills.List(AssistantRoles.Player));
        // Another role doesn't see it.
        Assert.StartsWith("There is no page", skills.Read(AssistantRoles.Admin, name, 0));
    }

    [Fact]
    public async Task ZipUploadPagesAreSkillAndSkillSlashFile()
    {
        var (harness, sources, skills) = await StartAsync();
        await using var _h = harness;
        var zip = Zip(
            ("skills/world/SKILL.md", World),
            ("skills/world/places.md", "# Places\n\nThe Moon.\n")
        );

        var view = await sources.SetAsync(
            new(
                AssistantRoles.Admin,
                "octahedron",
                SkillSourceKinds.Zip,
                Content: Convert.ToBase64String(zip),
                FileName: "skills.zip"
            ),
            default
        );

        Assert.Equal("skills.zip", view.FileName);
        Assert.Equal(SkillTree.Sha256(zip), view.Revision);
        Assert.Equal(World, skills.Read(AssistantRoles.Admin, "octahedron-world", 0));
        Assert.Equal(
            "# Places\n\nThe Moon.\n",
            skills.Read(AssistantRoles.Admin, "octahedron-world/places.md", 0)
        );
        var list = skills.List(AssistantRoles.Admin);
        Assert.Contains(
            "- octahedron-world: Places, factions and resources of the Octahedron world.",
            list
        );
        Assert.Contains("  - octahedron-world/places.md", list);
        Assert.Contains("octahedron-world/places.md", skills.Search(AssistantRoles.Admin, "Moon"));
        // The monitor reads the admin assistant's skills.
        Assert.Equal(World, skills.Read(AssistantRoles.Monitor, "octahedron-world", 0));
        Assert.StartsWith(
            "There is no page",
            skills.Read(AssistantRoles.Player, "octahedron-world", 0)
        );
    }

    [Theory]
    [InlineData("zip-without-skill", "No SKILL.md found.")]
    [InlineData("not-a-zip", "not a valid ZIP")]
    [InlineData("two-same-names", "Two skills are named same")]
    public async Task BadUploadsAreRefusedAndNothingIsSaved(string what, string message)
    {
        var (harness, sources, _) = await StartAsync();
        await using var _h = harness;
        var content = what switch
        {
            "zip-without-skill" => Zip(("docs/readme.md", "# Hi\n")),
            "not-a-zip" => Encoding.UTF8.GetBytes("PK but not really"),
            _ => Zip(
                ("a/SKILL.md", "---\nname: same\n---\n"),
                ("b/SKILL.md", "---\nname: same\n---\n")
            ),
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            sources.SetAsync(
                new(
                    AssistantRoles.Admin,
                    "pack",
                    SkillSourceKinds.Zip,
                    Content: Convert.ToBase64String(content)
                ),
                default
            )
        );

        Assert.Contains(message, error.Message);
        Assert.Empty(sources.List());
    }

    [Fact]
    public async Task SkillNamesStayUniquePerRole()
    {
        var (harness, sources, skills) = await StartAsync();
        await using var _h = harness;
        skills.Save(AssistantRoles.Admin, "rules", "# Written here\n");
        await sources.SetAsync(
            new(AssistantRoles.Admin, "one", SkillSourceKinds.File, Content: Base64("# One\n")),
            default
        );

        var written = await Assert.ThrowsAsync<ArgumentException>(() =>
            sources.SetAsync(
                new(
                    AssistantRoles.Admin,
                    "two",
                    SkillSourceKinds.File,
                    Content: Base64("---\nname: rules\n---\n")
                ),
                default
            )
        );
        var sourced = await Assert.ThrowsAsync<ArgumentException>(() =>
            sources.SetAsync(
                new(
                    AssistantRoles.Admin,
                    "two",
                    SkillSourceKinds.File,
                    Content: Base64("---\nname: one\n---\n")
                ),
                default
            )
        );
        Assert.Throws<ArgumentException>(() =>
            skills.Save(AssistantRoles.Admin, "one", "# Clash\n")
        );

        Assert.Contains("written on the settings page", written.Message);
        Assert.Contains("The source one already has a skill named one", sourced.Message);
        // The same name in another role is fine, and so is replacing a source with its own name.
        await sources.SetAsync(
            new(
                AssistantRoles.Player,
                "two",
                SkillSourceKinds.File,
                Content: Base64("---\nname: one\n---\n")
            ),
            default
        );
        await sources.SetAsync(
            new(
                AssistantRoles.Admin,
                "one",
                SkillSourceKinds.File,
                Content: Base64("# One again\n")
            ),
            default
        );
        Assert.Equal("# One again\n", skills.Read(AssistantRoles.Admin, "one", 0));
        // The monitor reads the admin assistant's skills, so it can't bring the same name.
        var monitor = await Assert.ThrowsAsync<ArgumentException>(() =>
            sources.SetAsync(
                new(
                    AssistantRoles.Monitor,
                    "m",
                    SkillSourceKinds.File,
                    Content: Base64("---\nname: one\n---\n")
                ),
                default
            )
        );
        Assert.Contains("the admin role already has a skill named one", monitor.Message);
    }

    [Fact]
    public async Task GitSourceFetchesPathsAtRefAndShowsTheCommit()
    {
        var (harness, sources, skills) = await StartAsync();
        await using var _h = harness;
        var repo = Repository(
            harness.Folder,
            ("octahedron/skills/octahedron-world/SKILL.md", World),
            ("octahedron/skills/octahedron-world/places.md", "# Places\n"),
            ("octahedron/skills/admin/SKILL.md", "---\nname: admin\n---\n# Admin\n"),
            ("support/SKILL.md", "---\nname: support\n---\n# Support\n"),
            ("support/sub/SKILL.md", "---\nname: sub\n---\n# Not taken\n"),
            ("README.md", "Not part of it.\n")
        );

        var view = await sources.SetAsync(
            new(
                AssistantRoles.Player,
                "octahedron",
                SkillSourceKinds.Git,
                Repository: "file://" + repo.Path,
                Ref: "main",
                Paths: ["octahedron/skills/octahedron-world", "support/SKILL.md"]
            ),
            default
        );

        Assert.Null(view.Error);
        Assert.Equal(repo.Commit, view.Revision);
        Assert.Equal(
            ["octahedron-world", "support"],
            view.Skills.Select(skill => skill.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            "octahedron/skills/octahedron-world",
            view.Skills.Single(skill => skill.Name == "octahedron-world").Folder
        );
        Assert.Equal(
            "# Places\n",
            skills.Read(AssistantRoles.Player, "octahedron-world/places.md", 0)
        );
        Assert.StartsWith("There is no page", skills.Read(AssistantRoles.Player, "admin", 0));
        Assert.StartsWith("There is no page", skills.Read(AssistantRoles.Player, "sub", 0));
    }

    [Fact]
    public async Task GitFolderUrlFillsRepositoryRefAndPath()
    {
        var (harness, sources, _) = await StartAsync();
        await using var _h = harness;
        var repo = Repository(harness.Folder, ("skills/world/SKILL.md", World));

        var view = await sources.SetAsync(
            new(
                AssistantRoles.Admin,
                "world",
                SkillSourceKinds.Git,
                Url: "file://" + repo.Path + "#main:skills"
            ),
            default
        );

        Assert.Equal("file://" + repo.Path, view.Repository);
        Assert.Equal("main", view.Ref);
        Assert.Equal(["skills"], view.Paths);
        Assert.Equal(["octahedron-world"], view.Skills.Select(skill => skill.Name));
    }

    [Fact]
    public async Task FailedFetchKeepsTheLastGoodSkills()
    {
        var (harness, sources, skills) = await StartAsync();
        await using var _h = harness;
        var repo = Repository(harness.Folder, ("skills/world/SKILL.md", World));
        await sources.SetAsync(
            new(
                AssistantRoles.Admin,
                "world",
                SkillSourceKinds.Git,
                Repository: "file://" + repo.Path,
                Ref: "main",
                Paths: ["skills/world"]
            ),
            default
        );
        Git(repo.Path, "rm", "-q", "-r", "skills");
        Git(repo.Path, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "gone");

        var view = await sources.RefreshAsync(AssistantRoles.Admin, "world", default);

        Assert.NotNull(view);
        Assert.Contains("There is no folder skills/world", view.Error);
        Assert.Equal(repo.Commit, view.Revision);
        Assert.Equal(World, skills.Read(AssistantRoles.Admin, "octahedron-world", 0));
    }

    [Theory]
    [InlineData("https://user:pw@github.com/o/r.git", null, "credentials")]
    [InlineData("http://github.com/o/r.git", null, "https")]
    [InlineData("https://github.com/o/r/tree/main/skills", null, "clone URL")]
    [InlineData("https://github.com/o/r.git", "../etc", "without . or ..")]
    public async Task BadGitSourcesAreRefused(string repository, string? path, string message)
    {
        var (harness, sources, _) = await StartAsync();
        await using var _h = harness;
        sources.AllowFileUrls = false;

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            sources.SetAsync(
                new(
                    AssistantRoles.Admin,
                    "x",
                    SkillSourceKinds.Git,
                    Repository: repository,
                    Paths: path is null ? null : [path]
                ),
                default
            )
        );

        Assert.Contains(message, error.Message);
        Assert.Empty(sources.List());
    }

    [Fact]
    public async Task OperationsNeedSecurityManagersAndAreClosedToAgents()
    {
        await using var harness = await Harness.StartAsync([]);
        var operations = harness
            .Services.GetServices<QuasarOperation>()
            .Where(operation =>
                operation.OperationId.StartsWith("settings.", StringComparison.Ordinal)
                || operation.OperationId.StartsWith("skills.", StringComparison.Ordinal)
            )
            .ToList();

        Assert.Equal(
            [
                SettingsApi.SetBackends,
                SettingsApi.Read,
                SettingsApi.SetRole,
                SettingsApi.ListSkills,
                SettingsApi.RefreshSource,
                SettingsApi.RemoveSource,
                SettingsApi.SetSource,
            ],
            operations.Select(operation => operation.OperationId).Order(StringComparer.Ordinal)
        );
        Assert.All(
            operations,
            operation =>
            {
                Assert.Equal([QuasarPolicyNames.CanManageSecurity], operation.DefaultPolicies);
                Assert.Equal(QuasarAgentAccess.Denied, operation.Metadata.AgentAccess);
            }
        );
    }

    [Fact]
    public async Task ApiSetsBackendsRolesAndSkillSources()
    {
        await using var harness = await Harness.StartAsync([]);

        var view = await Invoke<SettingsView>(
            harness,
            SettingsApi.SetBackends,
            """
            {"backends": [
               {"name": "claude", "kind": "Anthropic", "baseUrl": "https://api.anthropic.com/v1", "model": "claude-sonnet-5-5", "priority": 1},
               {"name": "local", "baseUrl": "http://127.0.0.1:8080/v1", "model": "qwen", "priority": 2, "parallel": 0}],
             "apiKeys": {"claude": "sk-test"}}
            """
        );
        Assert.Equal(["claude"], view.ApiKeys);
        Assert.Equal("sk-test", harness.Settings.ApiKey("claude"));
        Assert.Equal(1, harness.Settings.Current.Backends.Single(b => b.Name == "local").Parallel);

        view = await Invoke<SettingsView>(
            harness,
            SettingsApi.SetRole,
            """
            {"role": "player",
             "settings": {"backends": ["local", "claude"], "roleText": " Be kind. ", "areaModes": {"grids": "Apply"}, "maxTurns": 6},
             "player": {"enabled": true, "limits": {"maxMessages": 10}}}
            """
        );
        var player = view.Settings.Role(AssistantRoles.Player);
        Assert.Equal(["local", "claude"], player.Backends);
        Assert.Equal("Be kind.", player.RoleText);
        Assert.Equal(ToolMode.Apply, player.AreaModes["grids"]);
        Assert.Equal(6, player.MaxTurns);
        Assert.True(view.Settings.Player.Enabled);
        Assert.Equal(10, view.Settings.Player.Limits.MaxMessages);
        // Parts left out stay as they were.
        await Invoke<SettingsView>(
            harness,
            SettingsApi.SetRole,
            """{"role": "player", "player": {"enabled": false}}"""
        );
        Assert.Equal(
            ["local", "claude"],
            harness.Settings.Current.Role(AssistantRoles.Player).Backends
        );

        var source = await Invoke<SkillSourceView>(
            harness,
            SettingsApi.SetSource,
            JsonSerializer.Serialize(
                new
                {
                    role = "player",
                    name = "faq",
                    kind = "zip",
                    content = Convert.ToBase64String(Zip(("faq/SKILL.md", "# FAQ\n"))),
                }
            )
        );
        Assert.Equal(["faq"], source.Skills.Select(skill => skill.Name));
        var listed = await Invoke<List<RoleSkillsView>>(
            harness,
            SettingsApi.ListSkills,
            """{"role": "player"}"""
        );
        Assert.Equal("faq", listed.Single().Sources.Single().Name);
        await Invoke<JsonElement>(
            harness,
            SettingsApi.RemoveSource,
            """{"role": "player", "name": "faq"}"""
        );
        Assert.Empty(harness.Services.GetRequiredService<SkillSourceStore>().List());
    }

    [Theory]
    [InlineData(
        SettingsApi.SetRole,
        """{"role": "admin", "player": {"enabled": true}}""",
        400,
        "player role"
    )]
    [InlineData(
        SettingsApi.SetRole,
        """{"role": "admin", "settings": {"backends": ["nope"]}}""",
        400,
        "no backend named nope"
    )]
    [InlineData(
        SettingsApi.SetRole,
        """{"role": "boss"}""",
        400,
        "The role is admin, player, monitor"
    )]
    [InlineData(
        SettingsApi.SetBackends,
        """{"backends": [{"name": "a", "baseUrl": "ftp://x", "model": "m"}]}""",
        400,
        "http or https"
    )]
    [InlineData(
        SettingsApi.SetBackends,
        """{"backends": [], "apiKeys": {"ghost": "k"}}""",
        400,
        "no backend named ghost"
    )]
    [InlineData(
        SettingsApi.RemoveSource,
        """{"role": "admin", "name": "none"}""",
        404,
        "no skill source"
    )]
    [InlineData(
        SettingsApi.RefreshSource,
        """{"role": "admin", "name": "none"}""",
        404,
        "no skill source"
    )]
    [InlineData(
        SettingsApi.SetSource,
        """{"role": "admin", "name": "x", "kind": "folder"}""",
        400,
        "git, zip or file"
    )]
    public async Task ApiRefusesBadRequests(
        string operation,
        string body,
        int status,
        string message
    )
    {
        await using var harness = await Harness.StartAsync([]);
        var before = JsonSerializer.Serialize(harness.Settings.Current);

        var error = await Assert.ThrowsAsync<QuasarOperationException>(() =>
            Invoke<JsonElement>(harness, operation, body)
        );

        Assert.Equal(status, error.StatusCode);
        Assert.Contains(message, error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(harness.Settings.Current));
    }

    [Fact]
    public void SummaryOfAPageWithFrontMatterIsItsDescription()
    {
        Assert.Equal(
            "Places.",
            new SkillPage("x", "---\nname: x\ndescription: Places.\n---\n# Title\n").Summary
        );
        Assert.Equal("Title", new SkillPage("x", "---\nname: x\n---\n\n# Title\n").Summary);
        Assert.Equal("Title", new SkillPage("x", "# Title\n").Summary);
    }

    private static async Task<T> Invoke<T>(Harness harness, string operationId, string json)
    {
        var operation = harness
            .Services.GetServices<QuasarOperation>()
            .Single(operation => operation.OperationId == operationId);
        var admin = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "test")
        );
        var result = await operation.InvokeAsync(
            new QuasarOperationContext(admin, harness.Services),
            JsonDocument.Parse(json).RootElement,
            default
        );
        return JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(result, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web
        )!;
    }

    private static string Base64(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create))
            foreach (var (path, text) in files)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        return buffer.ToArray();
    }

    private static (string Path, string Commit) Repository(
        string parent,
        params (string Path, string Text)[] files
    )
    {
        var path = Path.Combine(parent, "repo-" + Guid.NewGuid().ToString("N"));
        foreach (var (file, text) in files)
        {
            var full = Path.Combine(path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Git(path, "init", "-q", "-b", "main");
        Git(path, "add", ".");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "skills");
        return (path, Git(path, "rev-parse", "HEAD").Trim());
    }

    private static string Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
