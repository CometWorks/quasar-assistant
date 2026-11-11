using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Lore;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;

namespace QuasarAssistant.Tests;

public sealed class LoreTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    // From the Octahedron world's lore data pads (SE-1308).
    private const string OctahedronLore = """
        # Octahedron lore

        Reactor supply memo: All our uranium shipments carry the same origin stamp: the Moon. Nobody has found a
        trace of it anywhere else.

        Ring miner's diary: Platinum again today. Funny how it only ever turns up out here in the ring.

        Water board memo: The survey charted four large ice asteroids in the ring.
        """;

    private static async Task<(Harness Harness, LoreStore Lore)> StartAsync()
    {
        var harness = await Harness.StartAsync(PlayerTests.Operations());
        harness.UsePlayerBackends(Kind, "p");
        var lore = harness.Services.GetRequiredService<LoreStore>();
        lore.AllowFileUrls = true;
        return (harness, lore);
    }

    private static QuasarChatCommandRequest Ask(
        PlayerAsk ask,
        string text,
        string server,
        string? node = null
    )
    {
        var request = ask.Game(text);
        return request with
        {
            Source = new QuasarChatCommandSource(QuasarChatChannel.Game, server, node),
        };
    }

    private static string Sent(Harness harness) =>
        string.Join("\n", harness.Server.Requests.Select(request => request.Body.GetRawText()));

    [Theory]
    [InlineData(
        "https://github.com/CometWorks/worlds/tree/main/octahedron/lore",
        "https://github.com/CometWorks/worlds.git",
        "main",
        "octahedron/lore"
    )]
    [InlineData(
        "https://gitlab.com/group/sub/repo/-/tree/v1.2/skills/lore/",
        "https://gitlab.com/group/sub/repo.git",
        "v1.2",
        "skills/lore"
    )]
    [InlineData(
        "https://git.cometworks.se/CometWorks/worlds/src/branch/main/lore",
        "https://git.cometworks.se/CometWorks/worlds.git",
        "main",
        "lore"
    )]
    [InlineData(
        "https://git.example.com/a/b.git#feature/x:docs/lore",
        "https://git.example.com/a/b.git",
        "feature/x",
        "docs/lore"
    )]
    [InlineData("https://git.example.com/a/b.git", "https://git.example.com/a/b.git", "", "")]
    public void GitUrlsGiveRepositoryRefAndPath(
        string text,
        string repository,
        string reference,
        string path
    )
    {
        Assert.True(GitSkillUrl.TryParse(text, false, out var url, out _));
        Assert.Equal(new GitSkillUrl(repository, reference, path), url);
    }

    [Theory]
    [InlineData("http://github.com/a/b")]
    [InlineData("file:///etc")]
    [InlineData("https://user:secret@github.com/a/b")]
    [InlineData("not a url")]
    public void OnlyHttpsUrlsWithoutCredentials(string text) =>
        Assert.False(GitSkillUrl.TryParse(text, false, out _, out _));

    [Fact]
    public async Task UnsetLoreChangesNothing()
    {
        var (harness, _) = await StartAsync();
        await using var _h = harness;
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "", [new ToolCall("c1", "skills_list", "{}")]),
            Replies.Answer(Kind, "No idea.")
        );
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(Ask(ask, "Where is uranium?", "alpha"), default);

        Assert.Equal("No idea.", ask.Replies[^1]);
        var sent = Sent(harness);
        Assert.DoesNotContain("lore: this server", sent);
        Assert.DoesNotContain("The lore of this server's world", sent);
    }

    [Fact]
    public async Task FreeTextLoreAnswersOnItsServerOnly()
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        await lore.SetAsync(new LoreSetRequest("Alpha", LoreKinds.Text, OctahedronLore), default);
        harness.Server.Enqueue(
            "p",
            Replies.Answer(
                Kind,
                "",
                [new ToolCall("c1", "skill_read", """{"name":"lore/world"}""")]
            ),
            Replies.Answer(Kind, "Uranium comes only from the Moon.")
        );
        var ask = new PlayerAsk();

        await harness.Players.HandleAsync(Ask(ask, "Where is uranium?", "alpha"), default);

        Assert.Equal("Uranium comes only from the Moon.", ask.Replies[^1]);
        var sent = Sent(harness);
        Assert.Contains("lore: this server has lore pages", sent);
        Assert.Contains("origin stamp: the Moon", sent);

        // Another server has no lore and doesn't see this one.
        var skills = harness.Services.GetRequiredService<SkillLibrary>();
        Assert.DoesNotContain("lore/", skills.List(AssistantRoles.Player, "beta"));
        Assert.StartsWith(
            "There is no page",
            skills.Read(AssistantRoles.Player, "lore/world", 0, "beta")
        );
    }

    [Fact]
    public async Task ClusterLoreHoldsOnEveryNode()
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        await lore.SetAsync(
            new LoreSetRequest("cluster:Octahedron", LoreKinds.Text, OctahedronLore),
            default
        );
        harness.Server.Enqueue(
            "p",
            Replies.Answer(Kind, "", [new ToolCall("c1", "docs_search", """{"query":"ice"}""")]),
            Replies.Answer(Kind, "Four ice asteroids in the ring.")
        );
        var ask = new PlayerAsk();

        // Core names the cluster, not the node, in a node's chat.
        await harness.Players.HandleAsync(
            Ask(ask, "Where is ice?", "octahedron", "node-3"),
            default
        );

        Assert.Contains("four large ice asteroids", Sent(harness));
    }

    [Fact]
    public async Task GitSkillGivesPagesAndKeepsAdminPagesFromPlayers()
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        var repo = Repository(
            harness.Folder,
            ("skills/lore/SKILL.md", "# Octahedron lore\n\nRead places.md for where things are.\n"),
            ("skills/lore/places.md", OctahedronLore),
            ("skills/lore/admin/secrets.md", "# Admin notes\n\nThe hidden vault is at 1,2,3.\n"),
            ("README.md", "Not part of the skill.\n")
        );

        var view = await lore.SetAsync(
            new LoreSetRequest(
                "alpha",
                LoreKinds.Git,
                GitUrl: "file://" + repo.Path + "#main:skills/lore"
            ),
            default
        );

        Assert.NotNull(view);
        Assert.Null(view.Error);
        Assert.Equal(repo.Commit, view.Revision);
        Assert.Equal(
            ["lore/SKILL", "lore/admin/secrets (admins only)", "lore/places"],
            view.Pages.Order(StringComparer.Ordinal)
        );
        var skills = harness.Services.GetRequiredService<SkillLibrary>();
        var player = skills.List(AssistantRoles.Player, "alpha");
        Assert.Contains("- lore/SKILL: Octahedron lore", player);
        Assert.Contains("- lore/places", player);
        Assert.DoesNotContain("lore/admin", player);
        Assert.DoesNotContain("README", player);
        Assert.StartsWith(
            "There is no page",
            skills.Read(AssistantRoles.Player, "lore/admin/secrets", 0, "alpha")
        );
        Assert.DoesNotContain("lore/admin", skills.Search(AssistantRoles.Player, "vault", "alpha"));
        Assert.Contains(
            "lore/admin/secrets",
            skills.Search(AssistantRoles.Admin, "vault", "alpha")
        );
        Assert.Contains(
            "hidden vault",
            skills.Read(AssistantRoles.Admin, "lore/admin/secrets", 0, "alpha")
        );
    }

    [Fact]
    public async Task FailingFetchIsReportedKeepsTheLastGoodCopyAndTheAssistantAnswers()
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        var missing = "file://" + Path.Combine(harness.Folder, "no-such-repo.git");

        var view = await lore.SetAsync(
            new LoreSetRequest("alpha", LoreKinds.Git, GitUrl: missing),
            default
        );

        Assert.NotNull(view?.Error);
        Assert.Contains("git fetch failed", view.Error);
        Assert.Empty(view.Pages);
        harness.Server.Enqueue("p", Replies.Answer(Kind, "Hello."));
        var ask = new PlayerAsk();
        await harness.Players.HandleAsync(Ask(ask, "Hi", "alpha"), default);
        Assert.Equal("Hello.", ask.Replies[^1]);
        Assert.DoesNotContain("lore: this server", Sent(harness));

        // A good fetch, then the repository goes away: the pages stay, with the error.
        var repo = Repository(harness.Folder, ("SKILL.md", OctahedronLore));
        var url = "file://" + repo.Path;
        Assert.Null(
            (await lore.SetAsync(new("alpha", LoreKinds.Git, GitUrl: url), default))!.Error
        );
        Directory.Delete(repo.Path, recursive: true);
        view = await lore.RefreshAsync("alpha", default);
        Assert.NotNull(view?.Error);
        Assert.Equal(repo.Commit, view.Revision);
        Assert.Contains(
            "origin stamp",
            harness
                .Services.GetRequiredService<SkillLibrary>()
                .Read(AssistantRoles.Player, "lore/SKILL", 0, "alpha")
        );
    }

    [Fact]
    public async Task TokensAreStoredEncryptedAndNeverShown()
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        const string token = "ghp_secretTokenValue1234567890";

        var view = await lore.SetAsync(
            new LoreSetRequest(
                "alpha",
                LoreKinds.Git,
                GitUrl: "file://" + Path.Combine(harness.Folder, "none.git"),
                Token: token
            ),
            default
        );

        Assert.True(view!.TokenSet);
        foreach (var file in Directory.EnumerateFiles(harness.Folder, "*.json"))
            Assert.DoesNotContain(token, File.ReadAllText(file));
        Assert.DoesNotContain(token, view.Error);
        // Switching to text drops the token.
        view = await lore.SetAsync(new("alpha", LoreKinds.Text, "Some lore."), default);
        Assert.False(view!.TokenSet);
    }

    [Fact]
    public async Task SettingNoneRemovesTheLore()
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        await lore.SetAsync(new("alpha", LoreKinds.Text, "Some lore."), default);

        Assert.Null(await lore.SetAsync(new("ALPHA", LoreKinds.None), default));

        Assert.Empty(lore.List());
        Assert.Empty(lore.Pages("alpha", AssistantRoles.Admin));
    }

    [Theory]
    [InlineData(LoreKinds.Text, "", null)]
    [InlineData(LoreKinds.Git, null, "http://example.com/a/b")]
    [InlineData("pdf", null, null)]
    public async Task InvalidInputIsRefused(string kind, string? text, string? gitUrl)
    {
        var (harness, lore) = await StartAsync();
        await using var _h = harness;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            lore.SetAsync(new("alpha", kind, text, gitUrl), default)
        );
        Assert.Empty(lore.List());
    }

    [Fact]
    public async Task OperationsNeedServerEditorsAndAreClosedToAgents()
    {
        await using var harness = await Harness.StartAsync([]);
        var operations = harness
            .Services.GetServices<QuasarOperation>()
            .Where(operation => operation.OperationId.StartsWith("lore.", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [LoreOperations.List, LoreOperations.Refresh, LoreOperations.Set],
            operations.Select(operation => operation.OperationId).Order()
        );
        Assert.All(
            operations,
            operation =>
            {
                Assert.Equal(AssistantServiceCollectionExtensions.PluginId, operation.PluginId);
                Assert.Equal([QuasarPolicyNames.CanEditServers], operation.DefaultPolicies);
                Assert.Equal(QuasarAgentAccess.Denied, operation.Metadata.AgentAccess);
            }
        );
    }

    /// <summary>A Git repository with one commit on <c>main</c>.</summary>
    private static (string Path, string Commit) Repository(
        string parent,
        params (string Path, string Text)[] files
    )
    {
        var path = System.IO.Path.Combine(parent, "repo-" + Guid.NewGuid().ToString("N"));
        foreach (var (file, text) in files)
        {
            var full = System.IO.Path.Combine(path, file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Git(path, "init", "-q", "-b", "main");
        Git(path, "add", ".");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "lore");
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
