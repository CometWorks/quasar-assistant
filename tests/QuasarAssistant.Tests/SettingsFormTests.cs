using QuasarAssistant.Settings;

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
        var form = RoleForm.From(new RoleSettings());
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
}
