using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuasarAssistant.Agent;

/// <summary>What the model gets from a tool: secrets replaced by <c>&lt;redacted&gt;</c>, cut to a size.</summary>
public static partial class ResultFilter
{
    public const string Redacted = "<redacted>";

    // Web names, as the operations write them; no HTML escaping, the model reads it.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Json(object? value)
    {
        var node = value as JsonNode ?? JsonSerializer.SerializeToNode(value, Options);
        return node is null ? "null" : RedactNode(node).ToJsonString(Options);
    }

    /// <summary>Secret-looking values in free text, such as a log line.</summary>
    public static string RedactText(string text)
    {
        text = Bearer().Replace(text, "${scheme} " + Redacted);
        text = ProviderKey().Replace(text, Redacted);
        return Assignment().Replace(text, "${name}${separator}" + Redacted);
    }

    /// <summary>Cuts text to <paramref name="capBytes"/> of UTF-8 and says that it did.</summary>
    public static string Cap(string text, int capBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        return bytes <= capBytes ? text : Cut(text, capBytes, $"{bytes} bytes");
    }

    /// <param name="size">How big the whole was, for the note.</param>
    public static string Cut(string text, int capBytes, string size)
    {
        var length = Math.Min(text.Length, capBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > capBytes)
            length -= Math.Max(1, length / 16);
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            length--;
        return text[..length]
            + $"\n[cut: the result was {size}, this is the first {capBytes} bytes. Ask for a smaller part, such as a tail or a filter.]";
    }

    public static bool IsSecretName(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", "").Replace("-", "");
        return lower.Contains("password")
            || lower.Contains("passwd")
            || lower.Contains("secret")
            || lower.Contains("token")
            || lower.Contains("apikey")
            || lower.Contains("credential")
            || lower.EndsWith("key", StringComparison.Ordinal);
    }

    /// <summary>Redacts in place and returns the node, or a replacement for a string value.</summary>
    private static JsonNode RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                    if (IsSecretName(name) && value is JsonValue)
                        obj[name] = Redacted;
                    else if (
                        value is not null
                        && RedactNode(value) is var redacted
                        && redacted != value
                    )
                        obj[name] = redacted;
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    if (
                        array[i] is { } item
                        && RedactNode(item) is var redacted
                        && redacted != item
                    )
                        array[i] = redacted;
                return array;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();
                var cleaned = RedactText(text);
                return cleaned == text ? value : JsonValue.Create(cleaned);
            default:
                return node;
        }
    }

    [GeneratedRegex(@"(?<scheme>\b[Bb]earer)\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    // sk-..., sk-ant-..., sk-or-v1-... and similar provider keys.
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}")]
    private static partial Regex ProviderKey();

    [GeneratedRegex(
        @"(?<name>\b[\w.-]*(?:password|passwd|secret|token|api[_-]?key)[\w.-]*[""']?)(?<separator>\s*[:=]\s*[""']?)(?!<redacted>)[^\s""',;]+",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Assignment();
}
