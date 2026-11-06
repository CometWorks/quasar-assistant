using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Tests;

public sealed class CodeExecutionTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    private static readonly JsonArray SandboxTools =
    [
        new JsonObject
        {
            ["name"] = "run_code",
            ["description"] = "Runs code.",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        },
        new JsonObject
        {
            ["name"] = "read_file",
            ["description"] = "Reads a file.",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        },
    ];

    private static bool HasSandbox(AssistantSettings settings, string role) =>
        settings
            .EffectiveRole(role)
            .McpServers.Any(s =>
                s.Name == SandboxSettings.ServerName
                && s.ReadOnlyTools.SequenceEqual(SandboxSettings.Tools)
            );

    [Fact]
    public void OnForTheAdminAssistantAndTheMonitorOffForPlayersByDefault()
    {
        var settings = new AssistantSettings
        {
            Sandbox = new() { Url = "http://127.0.0.1:8765/mcp" },
        };
        Assert.True(HasSandbox(settings, AssistantRoles.Admin));
        Assert.True(HasSandbox(settings, AssistantRoles.Monitor));
        Assert.False(HasSandbox(settings, AssistantRoles.Player));

        settings = settings
            .WithRole(AssistantRoles.Admin, new RoleSettings { CodeExecution = false })
            .WithRole(AssistantRoles.Player, new RoleSettings { CodeExecution = true });
        Assert.False(HasSandbox(settings, AssistantRoles.Admin));
        Assert.True(HasSandbox(settings, AssistantRoles.Player));

        // Without a sandbox nobody gets it, and the stored role never holds it.
        Assert.False(HasSandbox(settings with { Sandbox = new() }, AssistantRoles.Player));
        Assert.Empty(settings.Role(AssistantRoles.Player).McpServers);
    }

    [Fact]
    public void TheSandboxNameIsKept()
    {
        var form = RoleForm.From(new RoleSettings(), AssistantRoles.Admin);
        form.McpServers.Add(new McpServerForm { Name = "sandbox", Url = "http://x/mcp" });
        Assert.Contains("kept for code execution", form.Validate());
        Assert.False(RoleForm.From(new RoleSettings(), AssistantRoles.Player).CodeExecution);
    }

    [Fact]
    public async Task AdminAndMonitorRunCodeInReadModeWithTheSharedToken()
    {
        await using var sandbox = await StubMcpServer.StartAsync();
        sandbox.Tools = SandboxTools;
        await using var harness = await Harness.StartAsync([TestOperation.Read("servers.status")]);
        harness.UseBackends(Kind, "main");
        harness.Configure(s =>
            s.WithRole(
                AssistantRoles.Monitor,
                s.Role(AssistantRoles.Monitor) with
                {
                    Backends = ["main"],
                }
            ) with
            {
                Sandbox = new() { Url = sandbox.Url },
            }
        );
        harness.Settings.SetSandboxToken("sandbox-token");
        await harness.Services.GetRequiredService<McpRegistry>().RefreshAsync();
        Assert.Equal(
            2,
            harness.Services.GetRequiredService<McpRegistry>().Tools(AssistantRoles.Monitor).Count
        );
        Assert.Empty(
            harness.Services.GetRequiredService<McpRegistry>().Tools(AssistantRoles.Player)
        );

        var run = Replies.Answer(
            Kind,
            "",
            [
                new ToolCall(
                    "c1",
                    "mcp_sandbox_run_code",
                    "{\"language\":\"python\",\"code\":\"print(1)\"}"
                ),
            ]
        );
        harness.Server.Enqueue("main", run, Replies.Answer(Kind, "It printed 1."));
        var conversation = await harness.AskAsync("Compute it");
        Assert.Equal(["quasar.assistant/mcp.read"], harness.Dispatched.Keys);
        Assert.Empty(harness.Audit.Proposals);

        // The monitor calls it through its own operation, as the monitor.
        harness.Server.Enqueue("main", run, Replies.Answer(Kind, "Done."));
        var monitorConversation = harness.Conversations.Create(
            "agent:monitor",
            "Monitor",
            AssistantRoles.Monitor
        );
        await harness.Runner.AskAsync(
            monitorConversation,
            new AskRequest("Problem", [])
            {
                Agent = harness
                    .Services.GetRequiredService<IQuasarAgentPrincipals>()
                    .CreateMonitor(monitorConversation.Id, "Problem"),
            },
            default
        );
        var monitorCall = harness.Dispatched.Calls.Last();
        Assert.Equal("quasar.assistant/mcp.monitor.read", monitorCall.Key);
        Assert.True(monitorCall.User.IsQuasarAgent(out var agent));
        Assert.Equal(QuasarAgentKind.Monitor, agent.Kind);

        Assert.Equal(2, sandbox.Calls.Count);
        Assert.All(
            sandbox.Requests.Where(r => r.Method == "tools/call"),
            r => Assert.Equal("Bearer sandbox-token", r.Authorization)
        );
        Assert.Equal(
            [conversation.Id, monitorConversation.Id],
            sandbox.Calls.Select(c =>
                c["_meta"]!["quasar.assistant/conversation"]!.GetValue<string>()
            )
        );
    }
}
