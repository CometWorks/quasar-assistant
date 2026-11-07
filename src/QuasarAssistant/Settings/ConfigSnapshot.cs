using System.Text.Json;
using System.Text.Json.Nodes;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Settings;

/// <summary>The stored files of <see cref="SettingsStore"/>, secrets still encrypted.</summary>
public sealed record SettingsSnapshot(
    AssistantSettings Settings,
    IReadOnlyDictionary<string, string> Keys,
    IReadOnlyDictionary<string, string> McpHeaders
);

/// <summary>The assistant's whole configuration as the settings page found it: settings, API keys, MCP headers
/// and skills. Reverting puts it all back.</summary>
public sealed record ConfigSnapshot(
    SettingsSnapshot Store,
    IReadOnlyDictionary<string, IReadOnlyList<SkillPage>> Skills
)
{
    private const int ValueLength = 80;

    public static ConfigSnapshot Take(SettingsStore store, SkillLibrary skills) =>
        new(
            store.Snapshot(),
            AssistantRoles.All.ToDictionary(role => role, role => skills.Skills(role))
        );

    public void Restore(SettingsStore store, SkillLibrary skills)
    {
        foreach (var role in AssistantRoles.All)
        {
            var kept = Skills.GetValueOrDefault(role) ?? [];
            foreach (var page in skills.Skills(role))
                if (!kept.Any(old => old.Name == page.Name))
                    skills.Delete(role, page.Name);
            foreach (var page in kept)
                skills.Save(role, page.Name, page.Text);
        }
        store.Restore(Store);
    }

    /// <summary>What reverting from <paramref name="now"/> to this snapshot changes, one line each.</summary>
    public IReadOnlyList<string> Changes(ConfigSnapshot now)
    {
        var lines = new List<string>();
        var before = Flatten(JsonSerializer.SerializeToNode(Store.Settings, JsonFiles.Options));
        var after = Flatten(JsonSerializer.SerializeToNode(now.Store.Settings, JsonFiles.Options));
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(path, out var old);
            var has = after.TryGetValue(path, out var current);
            if (had && has && old != current)
                lines.Add($"{path}: {Short(current!)} back to {Short(old!)}");
            else if (had && !has)
                lines.Add($"{path}: back to {Short(old!)}");
            else if (!had && has)
                lines.Add($"{path}: {Short(current!)} removed");
        }
        Secrets(lines, "API key of the backend", Store.Keys, now.Store.Keys);
        Secrets(lines, "Headers of the MCP server", Store.McpHeaders, now.Store.McpHeaders);
        foreach (var role in AssistantRoles.All)
        {
            var old = (Skills.GetValueOrDefault(role) ?? []).ToDictionary(p => p.Name, p => p.Text);
            var current =
                now.Skills.GetValueOrDefault(role)?.ToDictionary(p => p.Name, p => p.Text) ?? [];
            foreach (var name in old.Keys.Union(current.Keys).Order(StringComparer.Ordinal))
                if (!current.TryGetValue(name, out var text))
                    lines.Add($"Skill {role}/{name}: restored");
                else if (!old.TryGetValue(name, out var was))
                    lines.Add($"Skill {role}/{name}: removed");
                else if (text != was)
                    lines.Add($"Skill {role}/{name}: its old text restored");
        }
        return lines;
    }

    // Encrypted values differ on every save, so a key typed again counts as changed.
    private static void Secrets(
        List<string> lines,
        string what,
        IReadOnlyDictionary<string, string> old,
        IReadOnlyDictionary<string, string> current
    )
    {
        foreach (var name in old.Keys.Union(current.Keys).Order(StringComparer.Ordinal))
            if (!current.ContainsKey(name))
                lines.Add($"{what} {name}: restored");
            else if (!old.TryGetValue(name, out var was))
                lines.Add($"{what} {name}: removed");
            else if (current[name] != was)
                lines.Add($"{what} {name}: the old one restored");
    }

    /// <summary>Leaf values by path; list items with a name are keyed by it, so a reorder reads as one change.</summary>
    private static Dictionary<string, string> Flatten(JsonNode? node)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(node, "", leaves);
        return leaves;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> leaves)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                    Walk(value, path.Length == 0 ? name : path + "." + name, leaves);
                break;
            case JsonArray array
                when array.All(item => item is JsonObject o && o["name"] is JsonValue)
                    && array.Select(item => (string?)item!["name"]!.ToString()).Distinct().Count()
                        == array.Count:
                foreach (var item in array)
                    Walk(item, $"{path}[{item!["name"]}]", leaves);
                break;
            default:
                leaves[path] = node?.ToJsonString(JsonFiles.Options) ?? "null";
                break;
        }
    }

    private static string Short(string value) =>
        value.Length > ValueLength ? value[..ValueLength] + "…" : value;
}
