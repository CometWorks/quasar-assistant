using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Agent;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Tests;

/// <summary>A streamable HTTP MCP server with a read tool and an acting tool.</summary>
public sealed class StubMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubMcpServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";
    public ConcurrentQueue<(
        string Method,
        string? Authorization,
        string? Session
    )> Requests { get; } = new();
    public ConcurrentQueue<JsonNode> Calls { get; } = new();

    /// <summary>The tools/list answer; tests change it to see a refresh.</summary>
    public JsonArray Tools { get; set; } =
    [
        new JsonObject
        {
            ["name"] = "restart",
            ["description"] = "Restarts the ticket system.",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        },
        new JsonObject
        {
            ["name"] = "lookup",
            ["description"] = "Looks up a ticket.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
            },
        },
    ];

    public static async Task<StubMcpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new StubMcpServer(app);
        app.MapPost("/mcp", server.HandleAsync);
        await app.StartAsync();
        server.Url =
            app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First() + "/mcp";
        return server;
    }

    private async Task HandleAsync(HttpContext http)
    {
        var message = (await JsonNode.ParseAsync(http.Request.Body))!;
        var method = message["method"]!.GetValue<string>();
        Requests.Enqueue(
            (method, http.Request.Headers.Authorization, http.Request.Headers["Mcp-Session-Id"])
        );
        if (message["id"] is null)
        {
            http.Response.StatusCode = 202;
            return;
        }
        JsonNode result = method switch
        {
            "initialize" => new JsonObject { ["protocolVersion"] = McpClient.ProtocolVersion },
            "tools/list" => new JsonObject { ["tools"] = Tools.DeepClone() },
            "tools/call" => Call(message["params"]!),
            _ => new JsonObject(),
        };
        var answer = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = message["id"]!.DeepClone(),
            ["result"] = result,
        };
        if (method == "initialize")
            http.Response.Headers["Mcp-Session-Id"] = "s1";
        if (method == "tools/list")
        {
            // Answered as an event stream, as streamable HTTP servers may.
            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync(
                "event: message\ndata: " + answer.ToJsonString() + "\n\n"
            );
            return;
        }
        await http.Response.WriteAsJsonAsync(answer);
    }

    private JsonNode Call(JsonNode parameters)
    {
        Calls.Enqueue(parameters.DeepClone());
        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "Ticket 7 is open. token=abc123secret",
                }
            ),
        };
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public sealed class McpTests
{
    private const BackendKind Kind = BackendKind.OpenAi;

    private static async Task<(Harness Harness, StubMcpServer Mcp)> StartAsync(
        Func<RoleSettings, RoleSettings>? role = null
    )
    {
        var mcp = await StubMcpServer.StartAsync();
        var harness = await Harness.StartAsync([TestOperation.Read("servers.status")]);
        harness.UseBackends(Kind, "main");
        harness.ConfigureRole(r =>
            (role ?? (x => x))(
                r with
                {
                    McpServers =
                    [
                        new McpServerSettings
                        {
                            Name = "ops",
                            Url = mcp.Url,
                            ReadOnlyTools = ["lookup"],
                        },
                    ],
                }
            )
        );
        harness.Settings.SetMcpHeaders(
            AssistantRoles.Admin,
            "ops",
            new Dictionary<string, string> { ["Authorization"] = "Bearer mcp-key" }
        );
        await harness.Services.GetRequiredService<McpRegistry>().RefreshAsync();
        return (harness, mcp);
    }

    [Fact]
    public async Task ToolsAreListedSortedWithTheServersHeaders()
    {
        var (harness, mcp) = await StartAsync();
        await using var _m = mcp;
        await using var _h = harness;

        var tools = await harness
            .Services.GetRequiredService<ToolCatalog>()
            .ForAsync(harness.Settings.Current.Role(AssistantRoles.Admin));

        var names = tools.Definitions.Select(t => t.Name).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("mcp_ops_lookup", names);
        Assert.Contains("mcp_ops_restart", names);
        var lookup = tools.Definitions.Single(t => t.Name == "mcp_ops_lookup");
        Assert.StartsWith("Looks up a ticket.", lookup.Description);
        Assert.True(lookup.Schema.GetProperty("properties").TryGetProperty("id", out _));
        Assert.Contains(
            mcp.Requests,
            r =>
                r.Method == "tools/list" && r.Authorization == "Bearer mcp-key" && r.Session == "s1"
        );
        // The headers are stored encrypted, never as plain text.
        Assert.DoesNotContain(
            "mcp-key",
            await File.ReadAllTextAsync(Path.Combine(harness.Folder, "mcp-headers.json"))
        );
    }

    [Fact]
    public async Task ReadOnlyToolRunsInReadMode()
    {
        var (harness, mcp) = await StartAsync(r =>
            r with
            {
                AreaModes = new() { ["mcp"] = ToolMode.Read },
            }
        );
        await using var _m = mcp;
        await using var _h = harness;
        harness.Server.Enqueue(
            "main",
            Replies.Answer(Kind, "", [new ToolCall("c1", "mcp_ops_lookup", "{\"id\":\"7\"}")]),
            Replies.Answer(Kind, "Ticket 7 is open.")
        );

        var conversation = await harness.AskAsync("Is ticket 7 open?");

        Assert.Equal(["quasar.assistant/mcp.read"], harness.Dispatched.Keys);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal("lookup", call["name"]!.GetValue<string>());
        Assert.Equal("7", call["arguments"]!["id"]!.GetValue<string>());
        var result = conversation.Messages.Single(m => m.Role == ChatRole.Tool);
        Assert.False(result.IsError);
        Assert.Contains("Ticket 7 is open", result.Text);
        Assert.DoesNotContain("abc123secret", result.Text);
        // In read mode the acting tool isn't offered at all.
        Assert.DoesNotContain(
            "mcp_ops_restart",
            PlayerTests.ToolNames(harness.Server.BodiesOf("main")[0])
        );
    }

    [Fact]
    public async Task ActingToolIsAlwaysAProposal()
    {
        var (harness, mcp) = await StartAsync(r =>
            r with
            {
                AreaModes = new() { ["mcp"] = ToolMode.Apply },
            }
        );
        await using var _m = mcp;
        await using var _h = harness;
        harness.Server.Enqueue(
            "main",
            Replies.Answer(
                Kind,
                "Restarting.",
                [new ToolCall("c1", "mcp_ops_restart", "{\"now\":true}")]
            ),
            Replies.Answer(Kind, "Proposed.")
        );

        var conversation = await harness.AskAsync("Restart the ticket system");

        Assert.Empty(harness.Dispatched.Calls);
        Assert.Empty(mcp.Calls);
        var (key, arguments, _) = Assert.Single(harness.Audit.Proposals);
        Assert.Equal("quasar.assistant/mcp.call", key);
        var request = JsonDocument.Parse(arguments).RootElement;
        Assert.Equal("ops", request.GetProperty("server").GetString());
        Assert.Equal("restart", request.GetProperty("tool").GetString());
        Assert.True(request.GetProperty("arguments").GetProperty("now").GetBoolean());
        var id = conversation.Messages.Single(m => m.Role == ChatRole.Tool).ProposalId!;
        var proposal = harness.Services.GetRequiredService<ProposalStore>().Get(id)!;
        Assert.Contains(proposal.Details!, line => line.StartsWith("Not reversible"));
    }

    [Fact]
    public async Task UnconfiguredServersAndToolsAreRefused()
    {
        var (harness, mcp) = await StartAsync();
        await using var _m = mcp;
        await using var _h = harness;
        var agent = await harness
            .Services.GetRequiredService<IQuasarAgentPrincipals>()
            .CreateAssistantAsync(Harness.Person(), "c", "r");
        var operations = harness
            .Services.GetServices<QuasarOperation>()
            .ToDictionary(op => op.OperationId);

        async Task<QuasarOperationResult> Call(string operation, string server, string tool) =>
            await ToolExecutor.DispatchAsync(
                harness.Services.GetRequiredService<IServiceScopeFactory>(),
                agent,
                operations[operation],
                JsonSerializer.SerializeToElement(
                    new McpCall(server, tool),
                    JsonSerializerOptions.Web
                ),
                default
            );

        Assert.Equal(403, (await Call("mcp.read", "other", "lookup")).StatusCode);
        Assert.Equal(404, (await Call("mcp.read", "ops", "nope")).StatusCode);
        // Not marked read-only: only mcp.call, so only as a proposal.
        Assert.Equal(403, (await Call("mcp.read", "ops", "restart")).StatusCode);
        Assert.Equal(200, (await Call("mcp.read", "ops", "lookup")).StatusCode);
        // A person can't call them directly.
        var person = await ToolExecutor.DispatchAsync(
            harness.Services.GetRequiredService<IServiceScopeFactory>(),
            Harness.Person(),
            operations["mcp.read"],
            JsonSerializer.SerializeToElement(
                new McpCall("ops", "lookup"),
                JsonSerializerOptions.Web
            ),
            default
        );
        Assert.Equal(403, person.StatusCode);
        // The acting operation can't be undone, as the proposal card says.
        Assert.Equal(QuasarReversal.NotReversible, operations["mcp.call"].Metadata.Reversal);
    }

    [Fact]
    public async Task PrefixStaysStableAcrossRequestsAndRefreshes()
    {
        var (harness, mcp) = await StartAsync();
        await using var _m = mcp;
        await using var _h = harness;
        var registry = harness.Services.GetRequiredService<McpRegistry>();
        harness.Server.Enqueue(
            "main",
            Replies.Answer(Kind, "One."),
            Replies.Answer(Kind, "Two."),
            Replies.Answer(Kind, "Three.")
        );

        await harness.AskAsync("one");
        var version = registry.Version;
        await registry.RefreshAsync();
        await harness.AskAsync("two", Harness.Person("Bob", "steam:2"));
        Assert.Equal(version, registry.Version);
        var bodies = harness.Server.BodiesOf("main");
        Assert.Equal(
            bodies[0].GetProperty("tools").GetRawText(),
            bodies[1].GetProperty("tools").GetRawText()
        );

        // A server that changes its tools changes the prefix once, at the next refresh.
        mcp.Tools.Add(new JsonObject { ["name"] = "close", ["description"] = "Closes a ticket." });
        await registry.RefreshAsync();
        await harness.AskAsync("three");
        Assert.Contains("mcp_ops_close", PlayerTests.ToolNames(harness.Server.BodiesOf("main")[2]));
    }

    [Fact]
    public async Task UnreachableServerKeepsItsTools()
    {
        var (harness, mcp) = await StartAsync();
        await using var _h = harness;
        var registry = harness.Services.GetRequiredService<McpRegistry>();
        await mcp.DisposeAsync();

        await registry.RefreshAsync();

        Assert.Equal(2, registry.Tools(AssistantRoles.Admin).Count);
        Assert.NotNull(registry.Status(AssistantRoles.Admin, "ops")!.Error);
    }
}
