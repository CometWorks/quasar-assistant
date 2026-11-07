using Microsoft.Extensions.DependencyInjection;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;

namespace QuasarAssistant.Tests;

public sealed class SettingsFormTests
{
    [Fact]
    public void PlayerFormKeepsEmptyLimitsAsNoLimit()
    {
        var form = PlayerForm.From(new PlayerSupportSettings());
        form.PerWeek = null;
        form.PerDay = 0;
        form.AllowedCauses.Add("hangar");

        var restore = form.ToSettings().Restore;

        Assert.Equal(0, restore.PerDay);
        Assert.Null(restore.PerWeek);
        Assert.Equal(4, restore.PerMonth);
        Assert.Equal(["destroyed", "cleanup", "hangar"], restore.AllowedCauses);
    }

    [Fact]
    public void RoleFormChecksTheMcpServers()
    {
        var form = RoleForm.From(new RoleSettings(), AssistantRoles.Admin);
        form.McpServers.Add(new McpServerForm { Name = "ops", Url = "https://mcp.example/mcp" });
        Assert.Null(form.Validate());

        form.McpServers.Add(new McpServerForm { Name = "ops", Url = "https://other.example/mcp" });
        Assert.Contains("same name", form.Validate());
        form.McpServers[1].Name = "a b";
        Assert.Contains("letters", form.Validate());
        form.McpServers[1].Name = "files";
        form.McpServers[1].Url = "file:///etc/passwd";
        Assert.Contains("http", form.Validate());
    }

    [Fact]
    public async Task RemovingAnMcpServerDropsItsHeaders()
    {
        await using var harness = await Harness.StartAsync([]);
        harness.ConfigureRole(role =>
            role with
            {
                McpServers = [new() { Name = "ops", Url = "http://127.0.0.1:1/mcp" }],
            }
        );
        harness.Settings.SetMcpHeaders(
            AssistantRoles.Admin,
            "ops",
            new Dictionary<string, string> { ["X-Key"] = "v" }
        );
        Assert.Equal(["X-Key"], harness.Settings.McpHeaderNames(AssistantRoles.Admin, "ops"));

        harness.ConfigureRole(role => role with { McpServers = [] });

        Assert.Empty(harness.Settings.McpHeaderNames(AssistantRoles.Admin, "ops"));
    }

    [Fact]
    public async Task RevertPutsBackSettingsKeysHeadersAndSkillsAndListsWhat()
    {
        await using var harness = await Harness.StartAsync([]);
        var store = harness.Settings;
        var skills = harness.Services.GetRequiredService<SkillLibrary>();
        store.SetApiKey("main", "old-key");
        skills.Save(AssistantRoles.Admin, "kept", "# Kept\n");
        skills.Save(AssistantRoles.Admin, "gone", "# Gone\n");
        var snapshot = ConfigSnapshot.Take(store, skills);
        Assert.Empty(snapshot.Changes(ConfigSnapshot.Take(store, skills)));

        store.Save(store.Current with { ResultCapBytes = 4096 });
        harness.ConfigureRole(role => role with { RoleText = "Be brief." });
        store.SetApiKey("main", "new-key");
        store.SetMcpHeaders(
            AssistantRoles.Admin,
            "ops",
            new Dictionary<string, string> { ["X"] = "v" }
        );
        skills.Save(AssistantRoles.Admin, "kept", "# Changed\n");
        skills.Delete(AssistantRoles.Admin, "gone");
        skills.Save(AssistantRoles.Admin, "new", "# New\n");

        var changes = snapshot.Changes(ConfigSnapshot.Take(store, skills));
        Assert.Contains(changes, line => line.StartsWith("resultCapBytes: 4096 back to "));
        Assert.Contains(changes, line => line.StartsWith("roles.admin.roleText: \"Be brief.\""));
        Assert.Contains("API key of the backend main: the old one restored", changes);
        Assert.Contains("Headers of the MCP server admin/ops: removed", changes);
        Assert.Contains("Skill admin/gone: restored", changes);
        Assert.Contains("Skill admin/kept: its old text restored", changes);
        Assert.Contains("Skill admin/new: removed", changes);

        snapshot.Restore(store, skills);

        Assert.Empty(snapshot.Changes(ConfigSnapshot.Take(store, skills)));
        Assert.Equal("old-key", store.ApiKey("main"));
        Assert.Empty(store.McpHeaderNames(AssistantRoles.Admin, "ops"));
        Assert.Equal(
            ["gone", "kept"],
            skills.Skills(AssistantRoles.Admin).Select(page => page.Name)
        );
        Assert.Equal(
            "# Kept\n",
            skills.Skills(AssistantRoles.Admin).Single(page => page.Name == "kept").Text
        );
    }

    [Fact]
    public void AnEmptySystemPromptIsLeftOut()
    {
        var request = new ChatRequest(
            "",
            [],
            [new ChatMessage { Role = ChatRole.User, Text = "1+1=" }]
        );
        Assert.False(AnthropicFormat.Body(new BackendSettings(), request).ContainsKey("system"));
        var messages = OpenAiFormat.Body(new BackendSettings(), request)["messages"]!.AsArray();
        Assert.Equal("user", (string?)Assert.Single(messages)!["role"]);
    }
}
