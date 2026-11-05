using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuasarAssistant.Llm;

/// <summary>The backend can't serve now; another backend may (Clangy's failover rule).</summary>
public sealed class BackendFailure(string reason, TimeSpan? retryAfter = null) : Exception(reason)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The backend refused the request itself (such as HTTP 400); another backend isn't tried.</summary>
public sealed class BackendRejected(string reason) : Exception(reason);

/// <summary>Sends one streaming request to one backend, in its API's format.</summary>
public sealed class LlmClient(HttpClient http)
{
    public const string AnthropicVersion = "2023-06-01";

    // Besides 5xx: the backend can't serve now (key, credit or model unavailable, timeout, rate limit), unlike a
    // request it rejects (400). Another backend may answer.
    private static readonly HashSet<int> FailedStatuses = [401, 402, 403, 404, 408, 429];

    /// <param name="onText">Gets the answer text so far, for the live view.</param>
    public async Task<ChatResponse> SendAsync(
        BackendSettings backend,
        string? apiKey,
        ChatRequest request,
        Action<string>? onText,
        CancellationToken token
    )
    {
        var anthropic = backend.Kind == BackendKind.Anthropic;
        var body = anthropic
            ? AnthropicFormat.Body(backend, request)
            : OpenAiFormat.Body(backend, request);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            backend.BaseUrl.TrimEnd('/') + (anthropic ? "/messages" : "/chat/completions")
        )
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in backend.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (anthropic)
                message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            else
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        if (anthropic && !message.Headers.Contains("anthropic-version"))
            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        using var timer = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        var started = false;
        Arm(timer, backend.FirstByteTimeout);
        try
        {
            using var response = await http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token
            );
            var status = (int)response.StatusCode;
            if (FailedStatuses.Contains(status) || status >= 500)
                throw new BackendFailure(
                    $"HTTP {status}: {await SnippetAsync(response, linked.Token)}",
                    RetryAfter(response)
                );
            if (!response.IsSuccessStatusCode)
                throw new BackendRejected(
                    $"HTTP {status}: {await SnippetAsync(response, linked.Token)}"
                );

            StreamParser parser = anthropic
                ? new AnthropicFormat.Parser()
                : new OpenAiFormat.Parser();
            using var reader = new StreamReader(
                await response.Content.ReadAsStreamAsync(linked.Token)
            );
            string? eventName = null;
            while (await reader.ReadLineAsync(linked.Token) is { } line)
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line[6..].Trim();
                    continue;
                }
                // Only data counts: OpenRouter sends keep-alive comments while a stuck provider is silent.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var data = line[5..].Trim();
                if (data == "[DONE]")
                    break;
                using var json = JsonDocument.Parse(data);
                if (eventName == "ping" || json.RootElement.GetString("type") == "ping")
                    continue; // Anthropic's keep-alive
                started = true;
                Arm(timer, backend.StallTimeout);
                if (parser.Accept(json.RootElement) is { } error)
                    throw new BackendFailure("stream error: " + error);
                if (parser.TextChanged)
                    onText?.Invoke(parser.Text.ToString());
            }
            return parser.Finish() ?? throw new BackendFailure("the stream ended early");
        }
        catch (OperationCanceledException)
            when (timer.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new BackendFailure(started ? "stall timeout" : "first-byte timeout");
        }
        catch (HttpRequestException error)
        {
            throw new BackendFailure("connection failed: " + error.Message);
        }
        catch (IOException error)
        {
            throw new BackendFailure("connection lost: " + error.Message);
        }
        catch (JsonException error)
        {
            throw new BackendFailure("unreadable stream: " + error.Message);
        }
    }

    private static void Arm(CancellationTokenSource timer, double? seconds) =>
        timer.CancelAfter(
            seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : Timeout.InfiniteTimeSpan
        );

    private static async Task<string> SnippetAsync(
        HttpResponseMessage response,
        CancellationToken token
    )
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(token);
            return text.Length > 500 ? text[..500] : text;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return response.ReasonPhrase ?? "";
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
}

/// <summary>Collects one streamed answer.</summary>
internal abstract class StreamParser
{
    public StringBuilder Text { get; } = new();
    public bool TextChanged { get; protected set; }

    /// <summary>Returns an error sent inside the stream, as OpenRouter does when a provider fails and Anthropic
    /// when it's overloaded.</summary>
    public abstract string? Accept(JsonElement data);

    /// <summary>Null when the stream ended without a stop reason.</summary>
    public abstract ChatResponse? Finish();

    protected void AddText(string? text)
    {
        TextChanged = !string.IsNullOrEmpty(text);
        Text.Append(text);
    }
}

internal static class JsonHelpers
{
    public static string? GetString(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int GetInt(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    public static JsonElement Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    /// <summary>Tool arguments as an object; text that isn't a JSON object becomes an empty one.</summary>
    public static JsonNode ArgumentsObject(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static void Merge(JsonObject body, JsonObject? extra)
    {
        if (extra is null)
            return;
        foreach (var (key, value) in extra)
            body[key] = value?.DeepClone();
    }

    /// <summary>Messages the model sees: errors left out, host notes put before the next user turn.</summary>
    public static IEnumerable<ChatMessage> ForModel(IEnumerable<ChatMessage> messages)
    {
        List<string> host = [];
        foreach (var message in messages)
            switch (message.Role)
            {
                case ChatRole.Error:
                    break;
                case ChatRole.Host:
                    host.Add(message.Text);
                    break;
                case ChatRole.User when host.Count > 0:
                    yield return message with
                    {
                        HostLines = [.. host, .. message.HostLines ?? []],
                    };
                    host.Clear();
                    break;
                default:
                    yield return message;
                    break;
            }
    }
}
