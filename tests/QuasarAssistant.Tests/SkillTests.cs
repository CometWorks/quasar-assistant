using Microsoft.Extensions.DependencyInjection;
using QuasarAssistant.Agent;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;

namespace QuasarAssistant.Tests;

public sealed class SkillTests
{
    private static async Task<(Harness Harness, SkillLibrary Skills)> StartAsync()
    {
        var harness = await Harness.StartAsync([]);
        var skills = harness.Services.GetRequiredService<SkillLibrary>();
        skills.Save(
            AssistantRoles.Admin,
            "restarts",
            "# Restart policy\n\nWe restart alpha every night at 04:00 UTC.\n"
        );
        skills.Save(
            AssistantRoles.Player,
            "faq",
            "# Server FAQ\n\nGrids over 5000 PCU are removed by the cleanup.\nNo PvP in the safe zone.\n"
        );
        return (harness, skills);
    }

    [Fact]
    public async Task ListShowsTheRolesSkillsAndTheBookForAdmins()
    {
        var (harness, skills) = await StartAsync();
        await using var _ = harness;

        var admin = skills.List(AssistantRoles.Admin);
        Assert.Contains("- restarts: Restart policy", admin);
        Assert.DoesNotContain("faq", admin);
        Assert.Contains("quasar-book/Backups", admin);

        var player = skills.List(AssistantRoles.Player);
        Assert.Contains("- faq: Server FAQ", player);
        Assert.DoesNotContain("restarts", player);
        Assert.DoesNotContain("quasar-book", player);
    }

    [Fact]
    public async Task PlayersGetTheBookOnlyWhenTheAdminsOpenIt()
    {
        var (harness, skills) = await StartAsync();
        await using var _ = harness;
        Assert.StartsWith(
            "There is no page",
            skills.Read(AssistantRoles.Player, "quasar-book/Backups", 0)
        );

        harness.Configure(s => s with { Player = s.Player with { QuasarBook = true } });

        Assert.Contains("quasar-book/Backups", skills.List(AssistantRoles.Player));
        Assert.DoesNotContain(
            "There is no page",
            skills.Read(AssistantRoles.Player, "quasar-book/Backups", 0)
        );
    }

    [Fact]
    public async Task SearchFindsPagesWithEveryWordIgnoringCase()
    {
        var (harness, skills) = await StartAsync();
        await using var _ = harness;

        var found = skills.Search(AssistantRoles.Player, "CLEANUP pcu");
        Assert.Contains("## faq", found);
        Assert.Contains("> Grids over 5000 PCU are removed by the cleanup.", found);
        Assert.StartsWith("Nothing found", skills.Search(AssistantRoles.Player, "cleanup nightly"));
        // Players never search the admins' skills.
        Assert.StartsWith("Nothing found", skills.Search(AssistantRoles.Player, "04:00"));
        Assert.Contains("## restarts", skills.Search(AssistantRoles.Admin, "04:00"));
        // Admins search the book too, a page at most once.
        var book = skills.Search(AssistantRoles.Admin, "backup");
        Assert.Contains("## quasar-book/", book);
    }

    [Fact]
    public async Task ReadIsCappedAndGoesOnFromAnOffset()
    {
        var (harness, skills) = await StartAsync();
        await using var _ = harness;
        harness.Configure(s => s with { ResultCapBytes = 1024 });
        skills.Save(
            AssistantRoles.Admin,
            "long",
            string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i}\n"))
        );

        var first = skills.Read(AssistantRoles.Admin, "long", 0);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(first) <= 1024);
        var next = int.Parse(
            System.Text.RegularExpressions.Regex.Match(first, @"offset (\d+)").Groups[1].Value
        );
        var second = skills.Read(AssistantRoles.Admin, "long", next);
        Assert.StartsWith(
            skills.Skills(AssistantRoles.Admin).Single(p => p.Name == "long").Text[next..][..20],
            second
        );
        Assert.StartsWith("There is no page", skills.Read(AssistantRoles.Admin, "../settings", 0));
    }

    [Fact]
    public void SkillNamesCantLeaveTheFolder()
    {
        Assert.False(SkillLibrary.IsValidName("../x"));
        Assert.False(SkillLibrary.IsValidName(""));
        Assert.True(SkillLibrary.IsValidName("faq_2"));
        Assert.Matches("^[0-9a-f]{40}$", SkillLibrary.BookCommit);
    }

    [Fact]
    public async Task SkillToolsAreOfferedToBothRoles()
    {
        var (harness, _) = await StartAsync();
        await using var _h = harness;
        var catalog = harness.Services.GetRequiredService<ToolCatalog>();

        var admin = (await catalog.ForAsync(new RoleSettings())).ByName.Keys;
        var player = (
            await catalog.ForAsync(new RoleSettings(), AssistantRoles.Player, canAct: false)
        )
            .ByName
            .Keys;

        Assert.Equal(["docs_search", "skill_read", "skills_list"], admin.Order());
        Assert.Equal(["ask_admin", "docs_search", "skill_read", "skills_list"], player.Order());
    }
}
