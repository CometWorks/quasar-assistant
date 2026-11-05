using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Mcp;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Agent;

public enum ToolKind
{
    /// <summary>A registered operation, dispatched with the model's arguments.</summary>
    Operation,

    /// <summary><c>my.grid.restore</c>: the model names the grid, the admin's rules decide the rest.</summary>
    Restore,

    /// <summary>A remote MCP server's tool, dispatched as <c>mcp.read</c> or <c>mcp.call</c>.</summary>
    Mcp,
    AskAdmin,
    SkillsList,
    DocsSearch,
    SkillRead,
}

/// <summary>A tool offered to the model.</summary>
/// <param name="Mode">For an acting MCP tool always <c>propose</c>.</param>
public sealed record AgentTool(string Name, ToolKind Kind, ToolMode Mode)
{
    /// <summary>The operation a call dispatches: the tool's own, or <c>mcp.read</c> / <c>mcp.call</c>.</summary>
    public QuasarOperation? Operation { get; init; }
    public McpTool? Mcp { get; init; }

    public bool IsChange =>
        Operation is not null && ToolCatalog.Effect(Operation) == QuasarOperationEffect.Change;
}

public sealed record ToolSet(
    IReadOnlyList<ToolDefinition> Definitions,
    IReadOnlyDictionary<string, AgentTool> ByName
);

/// <summary>
/// The tools a role may call, in a fixed order (sorted by name) so the prompt prefix stays byte-identical between
/// requests. The admin role gets the registered operations; the player role the player-scoped <c>my.*</c> ones
/// (none for a Discord user without a link). Both get their MCP servers' tools and the skill tools. A tool's
/// mode comes from the role's settings; core decides which operations agents may be offered at all.
/// </summary>
public sealed partial class ToolCatalog(
    IEnumerable<QuasarOperation> operations,
    IServiceScopeFactory scopes,
    McpRegistry mcp
)
{
    public const string RestoreKey = "quasar.core/my.grid.restore";
    private const string CorePluginId = "quasar.core";

    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private readonly IReadOnlyList<QuasarOperation> _operations = operations
        .Where(op => op.Kind is QuasarOperationKind.Json or QuasarOperationKind.Read)
        .OrderBy(op => op.Key, StringComparer.Ordinal)
        .ToList();

    // The last tool sets per role settings, by role variant, with what they were built from.
    private readonly ConditionalWeakTable<
        RoleSettings,
        ConcurrentDictionary<string, (string Signature, ToolSet Tools)>
    > _cache = [];

    /// <summary>The JSON and read operations core may offer to agents now, for the tools and the settings page.</summary>
    public async Task<IReadOnlyList<QuasarOperation>> AgentOperationsAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<IQuasarAgentOperations>();
        List<QuasarOperation> available = [];
        foreach (var operation in _operations)
            if (await agents.IsAvailableToAgentsAsync(operation.PluginId, operation.OperationId))
                available.Add(operation);
        return available;
    }

    /// <summary>The operations whose mode the role's settings set, for the settings page.</summary>
    public static IEnumerable<QuasarOperation> OfRole(
        IEnumerable<QuasarOperation> available,
        string roleName
    ) => available.Where(op => IsPlayerOperation(op) == (roleName == AssistantRoles.Player));

    /// <param name="canAct">False for a player without an agent principal (Discord without a link): no
    /// operation or MCP tools then.</param>
    public async Task<ToolSet> ForAsync(
        RoleSettings role,
        string roleName = AssistantRoles.Admin,
        bool canAct = true
    )
    {
        var available = await AgentOperationsAsync();
        var mcpTools = mcp.Tools(roleName);
        // An RBAC change can open or close operations, and a refresh change the MCP tools.
        var signature = mcp.Version + "\n" + string.Join('\n', available.Select(op => op.Key));
        var variant = roleName + "|" + canAct;
        var sets = _cache.GetOrCreateValue(role);
        if (sets.TryGetValue(variant, out var cached) && cached.Signature == signature)
            return cached.Tools;
        var tools = Build(role, roleName, canAct, available, mcpTools);
        sets[variant] = (signature, tools);
        return tools;
    }

    public static ToolMode EffectiveMode(QuasarOperation operation, RoleSettings role) =>
        role.OperationModes.TryGetValue(operation.Key, out var mode) ? mode
        : role.AreaModes.TryGetValue(Area(operation), out mode) ? mode
        : DefaultMode(operation);

    public static ToolMode DefaultMode(QuasarOperation operation) =>
        Effect(operation) == QuasarOperationEffect.Change ? ToolMode.Propose : ToolMode.Read;

    /// <summary>A read is offered in read, propose and apply; a change only in propose and apply.</summary>
    public static bool IsOffered(QuasarOperation operation, ToolMode mode) =>
        mode != ToolMode.Off
        && (mode != ToolMode.Read || Effect(operation) == QuasarOperationEffect.Read);

    public static string Area(QuasarOperation operation) => Resolved(operation).Area!;

    public static QuasarOperationEffect Effect(QuasarOperation operation) =>
        Resolved(operation).Effect!.Value;

    /// <summary>Player-scoped operations (<c>my.*</c>): only the player agent satisfies their policy.</summary>
    public static bool IsPlayerOperation(QuasarOperation operation) =>
        operation.DefaultPolicies.Contains(
            QuasarPolicyNames.CanActAsPlayer,
            StringComparer.OrdinalIgnoreCase
        );

    private static QuasarOperationMetadata Resolved(QuasarOperation operation) =>
        operation.Metadata.ResolveFor(operation.Kind, operation.OperationId);

    private static ToolSet Build(
        RoleSettings role,
        string roleName,
        bool canAct,
        IReadOnlyList<QuasarOperation> available,
        IReadOnlyList<McpTool> mcpTools
    )
    {
        var player = roleName == AssistantRoles.Player;
        List<(AgentTool Tool, string Description, JsonElement Schema)> tools = [];
        if (canAct)
        {
            foreach (var operation in OfRole(available, roleName))
            {
                if (operation.PluginId == McpRegistry.PluginId)
                    continue; // Offered as the MCP servers' own tools below.
                var mode = EffectiveMode(operation, role);
                if (!IsOffered(operation, mode))
                    continue;
                if (player && operation.Key == RestoreKey)
                {
                    var restore = new AgentTool("my_grid_restore", ToolKind.Restore, mode)
                    {
                        Operation = operation,
                    };
                    tools.Add((restore, RestoreDescription(mode), RestoreSchema));
                    continue;
                }
                var tool = new AgentTool(
                    player && operation.PluginId == CorePluginId
                        ? Shorten(NotAllowed().Replace(operation.OperationId, "_"), operation.Key)
                        : ToolName(operation),
                    ToolKind.Operation,
                    mode
                )
                {
                    Operation = operation,
                };
                tools.Add((tool, Describe(tool), Schema(operation)));
            }
            AddMcpTools(tools, role, roleName, available, mcpTools);
        }
        tools.Add(Local("skills_list", ToolKind.SkillsList, SkillsListText, Empty));
        tools.Add(Local("docs_search", ToolKind.DocsSearch, DocsSearchText, DocsSearchSchema));
        tools.Add(Local("skill_read", ToolKind.SkillRead, SkillReadText, SkillReadSchema));
        if (player)
            tools.Add(Local("ask_admin", ToolKind.AskAdmin, AskAdminText, AskAdminSchema));

        Dictionary<string, AgentTool> byName = [];
        List<ToolDefinition> definitions = [];
        foreach (var (tool, description, schema) in tools)
        {
            var named = tool;
            while (byName.ContainsKey(named.Name))
                named = named with { Name = Shorten(named.Name + "_", named.Name) };
            byName[named.Name] = named;
            definitions.Add(new ToolDefinition(named.Name, description, schema));
        }
        return new ToolSet(
            definitions.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList(),
            byName
        );
    }

    private static void AddMcpTools(
        List<(AgentTool, string, JsonElement)> tools,
        RoleSettings role,
        string roleName,
        IReadOnlyList<QuasarOperation> available,
        IReadOnlyList<McpTool> mcpTools
    )
    {
        // Each role calls its MCP servers through its own pair of operations.
        var read = available.FirstOrDefault(op =>
            op.Key == McpRegistry.PluginId + "/" + McpRegistry.ReadOperation(roleName)
        );
        var call = available.FirstOrDefault(op =>
            op.Key == McpRegistry.PluginId + "/" + McpRegistry.CallOperation(roleName)
        );
        foreach (var mcpTool in mcpTools)
        {
            var server = role.McpServers.FirstOrDefault(s => s.Enabled && s.Name == mcpTool.Server);
            if (server is null)
                continue;
            var readOnly = server.ReadOnlyTools.Contains(mcpTool.Name);
            var operation = readOnly ? read : call;
            if (operation is null)
                continue;
            var mode = EffectiveMode(operation, role);
            if (!IsOffered(operation, mode))
                continue;
            var tool = new AgentTool(
                Shorten(
                    NotAllowed().Replace($"mcp_{mcpTool.Server}_{mcpTool.Name}", "_"),
                    mcpTool.Server + "/" + mcpTool.Name
                ),
                ToolKind.Mcp,
                readOnly ? mode : ToolMode.Propose
            )
            {
                Operation = operation,
                Mcp = mcpTool,
            };
            var note = readOnly
                ? "\nFrom the MCP server " + mcpTool.Server + ". Reads only."
                : "\nFrom the MCP server "
                    + mcpTool.Server
                    + ". A call only creates a proposal that an admin approves or rejects later. It can't be undone.";
            tools.Add((tool, mcpTool.Description + note, ObjectSchema(mcpTool.Schema)));
        }
    }

    /// <summary><c>pluginId/operationId</c> as <c>[a-zA-Z0-9_-]{1,64}</c>; the tool set maps it back.</summary>
    internal static string ToolName(QuasarOperation operation) =>
        Shorten(
            NotAllowed().Replace(operation.PluginId + "__" + operation.OperationId, "_"),
            operation.Key
        );

    private static string Shorten(string name, string seed)
    {
        if (name.Length <= 64)
            return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..8];
        return name[..55] + "_" + hash;
    }

    private static string Describe(AgentTool tool)
    {
        var operation = tool.Operation!;
        var text = operation.Metadata.Description ?? operation.Key;
        if (!tool.IsChange)
            return text + "\nReads only.";
        var reversal = operation.Metadata.Reversal switch
        {
            QuasarReversal.Undo => " An admin can undo it from the audit log.",
            QuasarReversal.NothingToReverse => " There is nothing to reverse.",
            _ => " It can't be undone.",
        };
        return text
            + (
                tool.Mode == ToolMode.Apply
                    ? "\nChanges something and runs at once, unless the request comes from Discord, where it only creates a proposal."
                        + reversal
                    : "\nChanges something. A call only creates a proposal; an admin approves or rejects it later."
                        + reversal
            );
    }

    private static string RestoreDescription(ToolMode mode) =>
        "Restores one of the asking player's own grids that is gone from the world, from its newest backup before the loss. Give the gridId from my_grids or my_grid_losses. The server's rules set where it is placed and how many restores a player gets."
        + (
            mode == ToolMode.Apply
                ? "\nIt runs at once for the loss causes the admins allow; otherwise it becomes a request an admin approves or rejects."
                : "\nIt becomes a request an admin approves or rejects."
        )
        + " An admin can undo a restore.";

    private const string SkillsListText =
        "Lists the help pages you can read: the admins' skills and FAQ, with their first line, and the Quasar book's pages where you have it. Reads only.";
    private const string DocsSearchText =
        "Searches the help pages for words, ignoring case. Returns the pages that hold every word, most hits first, with a few matching lines each. Reads only.";
    private const string SkillReadText =
        "Reads one help page by the name skills_list or docs_search gave. Long pages come in parts; the result says which offset to read on from. Reads only.";
    private const string AskAdminText =
        "Hands this conversation to the server's admins: they see the player, your reason and the recent messages, and answer the player themselves. Use it when the player needs a person.";

    private static readonly JsonElement Empty = Parse("""{"type":"object","properties":{}}""");
    private static readonly JsonElement DocsSearchSchema = Parse(
        """{"type":"object","properties":{"query":{"type":"string","description":"One or more words."}},"required":["query"]}"""
    );
    private static readonly JsonElement SkillReadSchema = Parse(
        """{"type":"object","properties":{"name":{"type":"string"},"offset":{"type":"integer","description":"Character offset to start at; 0 or left out for the start."}},"required":["name"]}"""
    );
    private static readonly JsonElement AskAdminSchema = Parse(
        """{"type":"object","properties":{"reason":{"type":"string","description":"What the player needs, in one or two sentences."}},"required":["reason"]}"""
    );

    // The model only names the grid; placement and distances come from the admin's restore rules.
    private static readonly JsonElement RestoreSchema = Parse(
        """{"type":"object","properties":{"gridId":{"type":"integer","description":"The grid's ID from my_grids or my_grid_losses."}},"required":["gridId"]}"""
    );

    private static (AgentTool, string, JsonElement) Local(
        string name,
        ToolKind kind,
        string description,
        JsonElement schema
    ) => (new AgentTool(name, kind, ToolMode.Read), description, schema);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Schema(QuasarOperation operation)
    {
        var type = operation.RequestType;
        return ObjectSchema(
            type is null
                ? null
                : JsonSchemaExporter.GetJsonSchemaAsNode(
                    SchemaOptions,
                    type,
                    new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }
                )
        );
    }

    private static JsonElement ObjectSchema(JsonElement schema) =>
        ObjectSchema(JsonNode.Parse(schema.GetRawText()));

    private static JsonElement ObjectSchema(JsonNode? schema)
    {
        if (schema is not JsonObject root || !IsObjectType(root["type"]))
            root = new JsonObject { ["type"] = "object" };
        root["type"] = "object"; // Both APIs want a plain object at the root, never ["object", "null"].
        return JsonSerializer.SerializeToElement(root);
    }

    private static bool IsObjectType(JsonNode? type) =>
        type is JsonValue value
            ? value.TryGetValue<string>(out var text) && text == "object"
            : type is JsonArray array
                && array.Any(item =>
                    item is JsonValue v && v.TryGetValue<string>(out var t) && t == "object"
                );

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NotAllowed();
}
