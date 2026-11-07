using Microsoft.AspNetCore.DataProtection;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Settings;

/// <summary>
/// The plugin's settings in <c>settings.json</c>, and the backends' API keys in <c>keys.json</c>, encrypted with the
/// host's Data Protection keys as Quasar protects its own secrets. Keys are never read back to a page.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _settingsPath;
    private readonly string _keysPath;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();
    private AssistantSettings _settings;
    private Dictionary<string, string> _keys;

    public SettingsStore(AssistantPaths paths, IDataProtectionProvider protection)
    {
        _settingsPath = Path.Combine(paths.DataDirectory, "settings.json");
        _keysPath = Path.Combine(paths.DataDirectory, "keys.json");
        _protector = protection.CreateProtector("quasar.assistant.api-keys");
        _settings = JsonFiles.Read<AssistantSettings>(_settingsPath) ?? new();
        _keys = JsonFiles.Read<Dictionary<string, string>>(_keysPath) ?? [];
    }

    public event Action? Changed;

    public AssistantSettings Current
    {
        get
        {
            lock (_lock)
                return _settings;
        }
    }

    public void Save(AssistantSettings settings)
    {
        lock (_lock)
        {
            JsonFiles.Write(_settingsPath, settings);
            _settings = settings;
            // A removed backend takes its key along.
            var names = settings.Backends.Select(backend => backend.Name).ToHashSet();
            if (_keys.Keys.Any(name => !names.Contains(name)))
                SaveKeys(_keys.Where(pair => names.Contains(pair.Key)).ToDictionary());
        }
        Changed?.Invoke();
    }

    public bool HasApiKey(string backend)
    {
        lock (_lock)
            return _keys.ContainsKey(backend);
    }

    /// <summary>Null or empty removes the key.</summary>
    public void SetApiKey(string backend, string? key)
    {
        lock (_lock)
        {
            var keys = new Dictionary<string, string>(_keys);
            if (string.IsNullOrEmpty(key))
                keys.Remove(backend);
            else
                keys[backend] = _protector.Protect(key);
            SaveKeys(keys);
        }
    }

    public void RenameApiKey(string from, string to)
    {
        lock (_lock)
        {
            if (from == to || !_keys.TryGetValue(from, out var key))
                return;
            var keys = new Dictionary<string, string>(_keys) { [to] = key };
            keys.Remove(from);
            SaveKeys(keys);
        }
    }

    /// <summary>For the model clients only.</summary>
    internal string? ApiKey(string backend)
    {
        lock (_lock)
            return _keys.TryGetValue(backend, out var key) ? _protector.Unprotect(key) : null;
    }

    private void SaveKeys(Dictionary<string, string> keys)
    {
        JsonFiles.Write(_keysPath, keys);
        _keys = keys;
    }
}

/// <param name="DataDirectory">The plugin's own folder in the Quasar data folder.</param>
public sealed record AssistantPaths(string DataDirectory);
