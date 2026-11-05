namespace TemplatePlugin.Services;

public sealed class TemplatePluginState
{
    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

    public int RefreshCount { get; private set; }

    public event Action? Changed;

    public void Refresh()
    {
        RefreshCount++;
        Changed?.Invoke();
    }
}
