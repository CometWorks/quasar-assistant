using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Operations;
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
/// stays byte-identical between requests. A tool's mode comes from the role's settings; core decides which
/// operations agents may be offered at all, with its deny list and the current RBAC rules.
/// </summary>
public sealed partial class ToolCatalog(
    IEnumerable<QuasarOperation> operations,
    IServiceScopeFactory scopes,
    ILogger<ToolCatalog> logger
)
{
    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipStructDefaults } },
    };
    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        // Any JSON value, such as a JsonElement's: the APIs want an empty schema rather than true.
        TransformSchemaNode = (_, schema) =>
            schema.GetValueKind() == JsonValueKind.True ? new JsonObject() : schema,
    };
    private readonly IReadOnlyList<QuasarOperation> _operations = operations
        .Where(op => op.Kind is QuasarOperationKind.Json or QuasarOperationKind.Read)
        .OrderBy(op => op.Key, StringComparer.Ordinal)
        .ToList();

    // The last tool set per role settings, with the operations core allowed when it was built.
    private readonly ConditionalWeakTable<RoleSettings, Tuple<string, ToolSet>> _cache = [];

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

    public async Task<ToolSet> ForAsync(RoleSettings role)
    {
        var available = await AgentOperationsAsync();
        // An RBAC change can open or close operations, so the set is part of the cache key.
        var signature = string.Join('\n', available.Select(op => op.Key));
        if (_cache.TryGetValue(role, out var cached) && cached.Item1 == signature)
            return cached.Item2;
        var tools = Build(role, available);
        _cache.AddOrUpdate(role, Tuple.Create(signature, tools));
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

    private static QuasarOperationMetadata Resolved(QuasarOperation operation) =>
        operation.Metadata.ResolveFor(operation.Kind, operation.OperationId);

    private ToolSet Build(RoleSettings role, IReadOnlyList<QuasarOperation> available)
    {
        Dictionary<string, AgentTool> tools = [];
        List<ToolDefinition> definitions = [];
        foreach (var operation in available)
        {
            var mode = EffectiveMode(operation, role);
            if (!IsOffered(operation, mode) || SchemaOrNull(operation) is not { } schema)
                continue;
            var name = ToolName(operation);
            while (tools.ContainsKey(name))
                name = Shorten(name + "_", name);
            var tool = tools[name] = new AgentTool(name, operation, mode);
            definitions.Add(new ToolDefinition(name, Describe(tool), schema));
        }
        return new ToolSet(
            definitions.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList(),
            tools
        );
    }

    /// <summary>One plugin's odd request type must not take the other tools down with it.</summary>
    private JsonElement? SchemaOrNull(QuasarOperation operation)
    {
        try
        {
            return Schema(operation);
        }
        catch (Exception e)
        {
            logger.LogWarning(
                e,
                "The assistant leaves out {Operation}: its request schema can't be exported",
                operation.Key
            );
            return null;
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
        var text = tool.Operation.Metadata.Description ?? tool.Operation.Key;
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
        var type = operation.RequestType;
        JsonNode? schema = type is null
            ? null
            : JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, type, ExporterOptions);
        if (schema is not JsonObject root || !IsObjectType(root["type"]))
            root = new JsonObject { ["type"] = "object" };
        root["type"] = "object"; // Both APIs want a plain object at the root, never ["object", "null"].
        return JsonSerializer.SerializeToElement(root);
    }

    private static bool IsObjectType(JsonNode? type) =>
        type is JsonValue value
            ? value.GetValue<string>() == "object"
            : type is JsonArray array && array.Any(item => item?.GetValue<string>() == "object");

    /// <summary>
    /// The exporter writes a constructor parameter's default into the schema, but a struct parameter's
    /// <c>= default</c> reads back as null, which it can't serialize (core's <c>JsonElement Value = default</c>).
    /// Such a type is described without its constructor: no defaults, the parameters without one still required.
    /// </summary>
    private static void SkipStructDefaults(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !info.Properties.Any(HasStructDefault))
            return;
        foreach (var property in info.Properties)
            property.IsRequired = property.AssociatedParameter is { HasDefaultValue: false };
        info.CreateObject = () => RuntimeHelpers.GetUninitializedObject(info.Type);
    }

    private static bool HasStructDefault(JsonPropertyInfo property) =>
        property.AssociatedParameter is { HasDefaultValue: true, DefaultValue: null }
        && property.PropertyType.IsValueType
        && Nullable.GetUnderlyingType(property.PropertyType) is null;

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NotAllowed();
}
