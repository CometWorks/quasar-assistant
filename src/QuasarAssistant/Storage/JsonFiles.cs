using System.Text.Json;

namespace QuasarAssistant.Storage;

internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
            return default;
        using var file = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(file, Options);
    }

    /// <summary>Writes beside the file and moves it over, so a crash leaves the old or the new file.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
