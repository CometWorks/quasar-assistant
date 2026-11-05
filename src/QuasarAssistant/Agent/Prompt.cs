using System.Globalization;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Agent;

/// <summary>Where the asker was when they asked: the drawer's page and server.</summary>
public sealed record PageContext(string? PageUri, string? ServerName);

/// <summary>
/// The prompt prefix is byte-stable per role: a constant system prompt, the admins' role text and the tools.
/// Everything that varies (time, asker, page, server) goes into the user turn as <c>[host]</c> lines.
/// </summary>
public static class Prompt
{
    private static readonly string AdminBase = ReadBase("QuasarAssistant.system-prompt.md");
    private static readonly string PlayerBase = ReadBase("QuasarAssistant.player-system-prompt.md");

    public static string System(string roleName, RoleSettings role)
    {
        var text = roleName == AssistantRoles.Player ? PlayerBase : AdminBase;
        return string.IsNullOrWhiteSpace(role.RoleText)
            ? text
            : text + "\n# Notes from the admins\n\n" + role.RoleText.Trim() + "\n";
    }

    public static string Time(DateTimeOffset now) =>
        "time: " + now.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> HostLines(
        DateTimeOffset now,
        string askerName,
        string askerSubject,
        PageContext page
    )
    {
        List<string> lines = [Time(now), $"asker: {askerName} ({askerSubject})"];
        if (!string.IsNullOrWhiteSpace(page.PageUri))
            lines.Add("page: " + page.PageUri);
        if (!string.IsNullOrWhiteSpace(page.ServerName))
            lines.Add("server: " + page.ServerName);
        return lines;
    }

    private static string ReadBase(string name)
    {
        using var stream = typeof(Prompt).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
