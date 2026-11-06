using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Mcp;

/// <summary>The request of the <c>mcp.read</c> and <c>mcp.call</c> operations.</summary>
public sealed record McpCall(string Server, string Tool, JsonElement? Arguments = null);

public sealed record McpServerStatus(DateTimeOffset Time, int Tools, string? Error);

/// <summary>
/// The tools of each role's MCP servers. They are part of the cached prompt prefix, so they are fetched when the
/// settings change and on start, never per request; a server that can't be reached keeps the tools it had.
/// Every call goes through core's dispatcher as <c>quasar.assistant/mcp.read</c> or <c>mcp.call</c>; this
/// checks there that the server and tool are set up for the calling agent's role.
/// </summary>
public sealed class McpRegistry
{
    public const string PluginId = AssistantServiceCollectionExtensions.PluginId;

    /// <summary><c>mcp.read</c> for the admin assistant, <c>mcp.player.read</c> for the player agent.</summary>
    public static string ReadOperation(string role) =>
        role == AssistantRoles.Player ? "mcp.player.read" : "mcp.read";

    /// <summary><c>mcp.call</c> for the admin assistant, <c>mcp.player.call</c> for the player agent.</summary>
    public static string CallOperation(string role) =>
        role == AssistantRoles.Player ? "mcp.player.call" : "mcp.call";

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settings;
    private readonly McpClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<McpRegistry> _logger;
    private readonly SemaphoreSlim _refresh = new(1);
    private Dictionary<string, IReadOnlyList<McpTool>> _tools = [];
    private Dictionary<string, McpServerStatus> _status = [];
    private string _signature = "";

    public McpRegistry(
        SettingsStore settings,
        McpClient client,
        TimeProvider time,
        ILogger<McpRegistry> logger
    )
    {
        _settings = settings;
        _client = client;
        _time = time;
        _logger = logger;
        settings.Changed += () => _ = RefreshAsync();
    }

    /// <summary>Goes up whenever a role's tool list changes.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<McpTool> Tools(string role) => _tools.GetValueOrDefault(role) ?? [];

    public McpServerStatus? Status(string role, string server) =>
        _status.GetValueOrDefault(role + "/" + server);

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await _refresh.WaitAsync(token);
        try
        {
            var settings = _settings.Current;
            Dictionary<string, IReadOnlyList<McpTool>> tools = [];
            Dictionary<string, McpServerStatus> status = [];
            foreach (var role in AssistantRoles.All)
            {
                List<McpTool> list = [];
                foreach (var server in settings.Role(role).McpServers.Where(s => s.Enabled))
                {
                    var key = role + "/" + server.Name;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(ListTimeout);
                        var listed = await _client.ListToolsAsync(
                            server.Name,
                            server.Url,
                            _settings.McpHeaders(role, server.Name),
                            timeout.Token
                        );
                        list.AddRange(listed);
                        status[key] = new(_time.GetUtcNow(), listed.Count, null);
                    }
                    // A bad URL, an unreachable or broken server: keep what it had.
                    catch (Exception error) when (!token.IsCancellationRequested)
                    {
                        var kept = Tools(role).Where(tool => tool.Server == server.Name).ToList();
                        list.AddRange(kept);
                        status[key] = new(_time.GetUtcNow(), kept.Count, error.Message);
                        _logger.LogWarning(
                            "MCP server {Server} of the {Role} role failed to list its tools: {Error}",
                            server.Name,
                            role,
                            error.Message
                        );
                    }
                }
                tools[role] = list;
            }
            var signature = string.Join(
                '\n',
                tools.SelectMany(pair =>
                    pair.Value.Select(tool =>
                        $"{pair.Key}|{tool.Server}|{tool.Name}|{tool.Description}|{tool.Schema.GetRawText()}"
                    )
                )
            );
            _tools = tools;
            _status = status;
            if (signature != _signature)
            {
                _signature = signature;
                Version++;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>The handler of the MCP operations of <paramref name="role"/>. Only that role's agent calls
    /// them, for a server and tool set up for the role.</summary>
    public async Task<JsonNode?> CallAsync(
        ClaimsPrincipal user,
        string role,
        McpCall request,
        bool readOnly,
        CancellationToken token
    )
    {
        var kind =
            role == AssistantRoles.Player ? QuasarAgentKind.Player : QuasarAgentKind.Assistant;
        if (!user.IsQuasarAgent(out var agent) || agent.Kind != kind)
            throw new QuasarOperationException(
                403,
                role == AssistantRoles.Player
                    ? "Only the player agent calls the player's MCP tools."
                    : "Only the admin assistant calls the admins' MCP tools."
            );
        var server =
            _settings
                .Current.Role(role)
                .McpServers.FirstOrDefault(s => s.Enabled && s.Name == request.Server)
            ?? throw new QuasarOperationException(
                403,
                $"The MCP server {request.Server} is not set up for the {role} role."
            );
        if (!Tools(role).Any(tool => tool.Server == server.Name && tool.Name == request.Tool))
            throw new QuasarOperationException(
                404,
                $"The MCP server {server.Name} offers no tool {request.Tool}."
            );
        if (readOnly && !server.ReadOnlyTools.Contains(request.Tool))
            throw new QuasarOperationException(
                403,
                $"The MCP tool {request.Tool} is not marked read-only, so it can only be proposed."
            );
        try
        {
            return await _client.CallAsync(
                server.Url,
                _settings.McpHeaders(role, server.Name),
                request.Tool,
                request.Arguments is { } arguments ? JsonNode.Parse(arguments.GetRawText()) : null,
                token
            );
        }
        catch (Exception error)
            when (error is HttpRequestException or McpException or JsonException)
        {
            throw new QuasarOperationException(502, error.Message);
        }
    }
}
