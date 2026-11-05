using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Tests;

public sealed class PrefixTests
{
    private static List<QuasarOperation> Operations() =>
        [
            TestOperation.Change("servers.start"),
            TestOperation.Read("servers.status"),
            TestOperation.Read("logs.read"),
            TestOperation.Change("config.set"),
        ];

    [Theory]
    [InlineData(BackendKind.OpenAi)]
    [InlineData(BackendKind.Anthropic)]
    public async Task PrefixIsByteIdenticalAcrossAskersPagesAndTimes(BackendKind kind)
    {
        var time = new ManualTime();
        await using var harness = await Harness.StartAsync(Operations(), time);
        harness.UseBackends(kind, "main");
        harness.ConfigureRole(role => role with { RoleText = "We run two servers." });
        harness.Server.Enqueue("main", Replies.Answer(kind, "One."), Replies.Answer(kind, "Two."));

        await harness.AskAsync(
            "Status?",
            Harness.Person("Ada", "steam:1"),
            new PageContext("http://q/servers/alpha", "alpha")
        );
        time.Now = time.Now.AddHours(5);
        await harness.AskAsync(
            "Logs?",
            Harness.Person("Bob", "steam:2"),
            new PageContext("http://q/analytics", null)
        );

        var bodies = harness.Server.BodiesOf("main");
        Assert.Equal(2, bodies.Count);
        Assert.Equal(Prefix(kind, bodies[0]), Prefix(kind, bodies[1]));
        // The variable context is in the user turn instead.
        Assert.Contains("[host] asker: Ada (steam:1)", bodies[0].GetRawText());
        Assert.Contains("[host] asker: Bob (steam:2)", bodies[1].GetRawText());
    }

    [Fact]
    public async Task ToolsAreSortedByName()
    {
        await using var harness = await Harness.StartAsync(Operations());
        var tools = await harness
            .Services.GetRequiredService<ToolCatalog>()
            .ForAsync(new RoleSettings());
        var names = tools.Definitions.Select(tool => tool.Name).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(
            [
                "docs_search",
                "quasar_core__config_set",
                "quasar_core__logs_read",
                "quasar_core__servers_start",
                "quasar_core__servers_status",
                "skill_read",
                "skills_list",
            ],
            names
        );
        // The tool set maps names back to the operation.
        Assert.Equal(
            "quasar.core/servers.start",
            tools.ByName["quasar_core__servers_start"].Operation!.Key
        );
    }

    [Fact]
    public void LongNamesAreShortenedToTheApiLimit()
    {
        var operation = TestOperation.Read("a" + new string('b', 100));
        var name = ToolCatalog.ToolName(operation);
        Assert.Equal(64, name.Length);
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", name);
    }

    public sealed record SetMotdRequest(string Server, string Text, int? Seconds);

    [Fact]
    public async Task SchemaAndDescriptionComeFromTheOperation()
    {
        var services = new ServiceCollection();
        services.AddQuasarOperation<SetMotdRequest, object>(
            "quasar.core",
            "servers.motd.set",
            [QuasarPolicyNames.CanEditConfigs],
            (_, _, _) => Task.FromResult<object>(new { }),
            new QuasarOperationMetadata { Description = "Sets the message of the day of a server." }
        );
        var operation = services.BuildServiceProvider().GetRequiredService<QuasarOperation>();
        await using var harness = await Harness.StartAsync([operation]);
        var tool = (
            await harness.Services.GetRequiredService<ToolCatalog>().ForAsync(new RoleSettings())
        ).Definitions.Single(tool => tool.Name.StartsWith("quasar_core", StringComparison.Ordinal));
        Assert.StartsWith("Sets the message of the day of a server.\n", tool.Description);
        Assert.Equal("object", tool.Schema.GetProperty("type").GetString());
        var properties = tool.Schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("server", out _));
        Assert.True(properties.TryGetProperty("text", out _));
        Assert.Contains("proposal", tool.Description);
    }

    // The part of the request a provider caches: the system prompt and the tools.
    private static string Prefix(BackendKind kind, JsonElement body) =>
        kind == BackendKind.Anthropic
            ? body.GetProperty("system").GetRawText() + body.GetProperty("tools").GetRawText()
            : body.GetProperty("messages")[0].GetRawText() + body.GetProperty("tools").GetRawText();

    [Fact]
    public async Task OperationsCoreClosesToAgentsAreNotOffered()
    {
        await using var harness = await Harness.StartAsync(Operations());
        var catalog = harness.Services.GetRequiredService<ToolCatalog>();
        var role = new RoleSettings();
        Assert.Contains("quasar_core__logs_read", (await catalog.ForAsync(role)).ByName.Keys);

        // An RBAC change that closes the operation to agents takes effect on the next run.
        harness.Dispatched.Unavailable.Add("quasar.core/logs.read");
        Assert.DoesNotContain("quasar_core__logs_read", (await catalog.ForAsync(role)).ByName.Keys);
    }
}
