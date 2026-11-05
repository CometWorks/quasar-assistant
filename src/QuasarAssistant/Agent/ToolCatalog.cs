using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Agent;

/// <summary>An operation offered to the model as a tool.</summary>
public sealed record AgentTool(string Name, QuasarOperation Operation, ToolMode Mode)
{
    public bool IsChange => ToolCatalog.Effect(Operation) == QuasarOperationEffect.Change;
}

public sealed record ToolSet(
    IReadOnlyList<ToolDefinition> Definitions,
    IReadOnlyDictionary<string, AgentTool> ByName
);

/// <summary>
/// The registered operations a role may call, as tools in a fixed order (sorted by name) so the prompt prefix
/// stays byte-identical between requests. A tool's mode comes from the role's settings; the operations core
/// refuses to any agent are left out.
/// </summary>
public sealed partial class ToolCatalog(IEnumerable<QuasarOperation> operations)
{
    // Mirrors the dispatcher's hard deny list for agents (QuasarOperationService in core). Core has no public
    // method to ask, so this keeps such operations out of the prompt; the dispatcher still refuses them on a call.
    private static readonly string[] AgentDeniedPrefixes =
    [
        "security.",
        "rbac.",
        "auth.",
        "service-principals.",
        "update.",
        "updates.",
        "workshop.credentials",
        "discord.token",
        "assistant.settings",
        "llm.",
    ];

    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private readonly IReadOnlyList<QuasarOperation> _operations = operations
        .OrderBy(op => op.Key, StringComparer.Ordinal)
        .ToList();
    private readonly ConditionalWeakTable<RoleSettings, ToolSet> _cache = [];

    /// <summary>Operations an agent may ever reach, with their area, for the settings page.</summary>
    public IEnumerable<QuasarOperation> AgentOperations => _operations.Where(IsAgentReachable);

    public ToolSet For(RoleSettings role) => _cache.GetValue(role, Build);

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

    private static QuasarOperationMetadata Resolved(QuasarOperation operation) =>
        operation.Metadata.ResolveFor(operation.Kind, operation.OperationId);

    private static bool IsAgentReachable(QuasarOperation operation) =>
        operation.Kind is QuasarOperationKind.Json or QuasarOperationKind.Read
        && operation.Metadata.AgentAccess == QuasarAgentAccess.Allowed
        && !operation.DefaultPolicies.Any(policy =>
            policy.Equals(QuasarPolicyNames.CanManageSecurity, StringComparison.OrdinalIgnoreCase)
            || policy.Equals(
                QuasarPolicyNames.CanShutdownQuasar,
                StringComparison.OrdinalIgnoreCase
            )
        )
        && !AgentDeniedPrefixes.Any(prefix =>
            operation.OperationId.StartsWith(prefix, StringComparison.Ordinal)
        );

    private ToolSet Build(RoleSettings role)
    {
        Dictionary<string, AgentTool> tools = [];
        foreach (var operation in AgentOperations)
        {
            var mode = EffectiveMode(operation, role);
            if (!IsOffered(operation, mode))
                continue;
            var name = ToolName(operation);
            while (tools.ContainsKey(name))
                name = Shorten(name + "_", name);
            tools[name] = new AgentTool(name, operation, mode);
        }
        var definitions = tools
            .Values.OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => new ToolDefinition(tool.Name, Describe(tool), Schema(tool.Operation)))
            .ToList();
        return new ToolSet(definitions, tools);
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
        var text = OperationInfo.Description(tool.Operation) ?? tool.Operation.Key;
        if (!tool.IsChange)
            return text + "\nReads only.";
        var reversal = tool.Operation.Metadata.Reversal switch
        {
            QuasarReversal.Undo => " An admin can undo it from the audit log.",
            QuasarReversal.NothingToReverse => " There is nothing to reverse.",
            _ => " It can't be undone.",
        };
        return text
            + (
                tool.Mode == ToolMode.Apply
                    ? "\nChanges something and runs at once." + reversal
                    : "\nChanges something. A call only creates a proposal; an admin approves or rejects it later."
                        + reversal
            );
    }

    private static JsonElement Schema(QuasarOperation operation)
    {
        var type = OperationInfo.RequestType(operation);
        JsonNode? schema = type is null
            ? null
            : JsonSchemaExporter.GetJsonSchemaAsNode(
                SchemaOptions,
                type,
                new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }
            );
        if (schema is not JsonObject root || !IsObjectType(root["type"]))
            root = new JsonObject { ["type"] = "object" };
        root["type"] = "object"; // Both APIs want a plain object at the root, never ["object", "null"].
        return JsonSerializer.SerializeToElement(root);
    }

    private static bool IsObjectType(JsonNode? type) =>
        type is JsonValue value
            ? value.GetValue<string>() == "object"
            : type is JsonArray array && array.Any(item => item?.GetValue<string>() == "object");

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NotAllowed();
}
