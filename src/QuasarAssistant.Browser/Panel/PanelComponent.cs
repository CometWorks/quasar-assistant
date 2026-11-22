using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace QuasarAssistant.Panel;

/// <summary>Each mounted component owns its requests and its optional live subscription.</summary>
public abstract class PanelComponent : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;
    private Task<IJSObjectReference>? _client;
    private DotNetObjectReference<PanelComponent>? _receiver;
    private Func<Task>? _refresh;
    protected string? PanelError { get; set; }
    protected bool Disposed { get; private set; }

    private async Task<IJSObjectReference> Open()
    {
        var url = Navigation.ToAbsoluteUri("_quasar/plugins/quasar.assistant/panel.js").AbsoluteUri;
        await using var module = await JS.InvokeAsync<IJSObjectReference>("import", url);
        return await module.InvokeAsync<IJSObjectReference>("open");
    }

    protected static bool ValidName(string name) =>
        name.Length is > 0 and <= 64
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    protected async Task Upload(string operation, object input) =>
        await (await (_client ??= Open())).InvokeVoidAsync("upload", operation, Encode(input));

    protected async Task UploadFile(string operation, object input, ElementReference file) =>
        await (await (_client ??= Open())).InvokeVoidAsync(
            "uploadFile",
            operation,
            Encode(input),
            file
        );

    protected async Task<T> Upload<T>(string operation, object input) =>
        (T)
            JsonSerializer.Deserialize(
                await (await (_client ??= Open())).InvokeAsync<string>(
                    "upload",
                    operation,
                    Encode(input)
                ),
                typeof(T),
                PanelJson.Default
            )!;

    protected static string Encode(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), PanelJson.Default);

    protected async Task<string> Request(string operation, object? input = null) =>
        await (await (_client ??= Open())).InvokeAsync<string>(
            "request",
            operation,
            input is null ? "{}" : Encode(input)
        );

    protected async Task<T> Read<T>(string operation, object? input = null) =>
        (T)
            JsonSerializer.Deserialize(
                await Request(operation, input),
                typeof(T),
                PanelJson.Default
            )!;

    protected async Task Watch(string topic, Func<Task> refresh)
    {
        _refresh = refresh;
        _receiver ??= DotNetObjectReference.Create<PanelComponent>(this);
        await (await (_client ??= Open())).InvokeVoidAsync("watch", topic, _receiver);
    }

    [JSInvokable]
    public async Task FeedChanged()
    {
        if (!Disposed && _refresh is not null)
            await InvokeAsync(async () =>
            {
                await Try(_refresh);
                StateHasChanged();
            });
    }

    [JSInvokable]
    public Task Failed(string error) =>
        InvokeAsync(() =>
        {
            PanelError = error;
            StateHasChanged();
        });

    protected async Task Try(Func<Task> action)
    {
        try
        {
            PanelError = null;
            await action();
        }
        catch (Exception e) when (!Disposed)
        {
            PanelError = e.Message;
        }
        catch (Exception) when (Disposed) { }
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        try
        {
            if (_client is not null)
            {
                await using var client = await _client;
                await client.InvokeVoidAsync("close");
            }
        }
        finally
        {
            _receiver?.Dispose();
        }
    }
}
