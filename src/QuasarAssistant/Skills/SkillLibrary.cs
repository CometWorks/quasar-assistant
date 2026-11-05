using System.Text;
using System.Text.RegularExpressions;
using QuasarAssistant.Settings;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Skills;

public sealed record SkillPage(string Name, string Text)
{
    /// <summary>The first non-empty line without its Markdown heading marks.</summary>
    public string Summary =>
        Text.Split('\n')
            .Select(line => line.Trim().TrimStart('#').Trim())
            .FirstOrDefault(line => line.Length > 0)
        ?? "";
}

/// <summary>
/// The knowledge the agents read through tools: the Quasar book shipped in the plugin, and the admins' Markdown
/// skills per role in <c>skills/&lt;role&gt;/*.md</c> of the data folder. Text only; nothing here runs.
/// </summary>
public sealed partial class SkillLibrary(AssistantPaths paths, SettingsStore settings)
{
    public const string BookPrefix = "quasar-book/";
    private const int MaxSearchPages = 20;
    private const int SnippetsPerPage = 3;

    private static readonly IReadOnlyList<SkillPage> Book = ReadBook();

    /// <summary>The Quasar commit the shipped book was taken from.</summary>
    public static string BookCommit { get; } =
        ReadResource("QuasarAssistant.QuasarBook.COMMIT").Trim();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>The role's own skills, editable on the settings page.</summary>
    public IReadOnlyList<SkillPage> Skills(string role)
    {
        var folder = Folder(role);
        return Directory.Exists(folder)
            ? Directory
                .EnumerateFiles(folder, "*.md")
                .Select(path => new SkillPage(
                    Path.GetFileNameWithoutExtension(path),
                    File.ReadAllText(path).ReplaceLineEndings("\n")
                ))
                .OrderBy(page => page.Name, StringComparer.Ordinal)
                .ToList()
            : [];
    }

    public void Save(string role, string name, string text)
    {
        if (!IsValidName(name))
            throw new ArgumentException("A skill name is 1 to 64 letters, digits, - or _.");
        Directory.CreateDirectory(Folder(role));
        File.WriteAllText(FilePath(role, name), text.ReplaceLineEndings("\n"));
    }

    public void Delete(string role, string name)
    {
        if (IsValidName(name))
            File.Delete(FilePath(role, name));
    }

    /// <summary>What the role may read: its skills, and the book for admins or when the admins open it to players.</summary>
    public IReadOnlyList<SkillPage> PagesFor(string role) =>
        WithBook(role) ? [.. Skills(role), .. Book] : Skills(role);

    /// <summary><c>skills_list</c>: name and first line per skill, and the book's contents for those who read it.</summary>
    public string List(string role)
    {
        var text = new StringBuilder();
        var skills = Skills(role);
        text.Append(skills.Count == 0 ? "No skills written by the admins.\n" : "Skills:\n");
        foreach (var page in skills)
            text.Append($"- {page.Name}: {page.Summary}\n");
        if (WithBook(role))
        {
            text.Append($"\nThe Quasar book, pages named {BookPrefix}<Page>:\n");
            foreach (var page in Book)
                text.Append($"- {page.Name}: {page.Summary}\n");
            if (Book.FirstOrDefault(page => page.Name == BookPrefix + "TOC") is { } toc)
                text.Append("\n").Append(toc.Text);
        }
        return text.ToString();
    }

    /// <summary><c>docs_search</c>: pages holding every word of the query, most hits first, with a few lines each.</summary>
    public string Search(string role, string query)
    {
        var words = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (words.Count == 0)
            return "Give one or more words to search for.";
        var hits = PagesFor(role)
            .Select(page => (Page: page, Count: words.Sum(word => Count(page.Text, word))))
            .Where(hit =>
                words.All(word => hit.Page.Text.Contains(word, StringComparison.OrdinalIgnoreCase))
            )
            .OrderByDescending(hit => hit.Count)
            .ThenBy(hit => hit.Page.Name, StringComparer.Ordinal)
            .ToList();
        if (hits.Count == 0)
            return $"Nothing found for \"{query}\".";
        var text = new StringBuilder();
        foreach (var (found, _) in hits.Take(MaxSearchPages))
        {
            text.Append($"## {found.Name}\n");
            foreach (
                var line in found
                    .Text.Split('\n')
                    .Where(line =>
                        words.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase))
                    )
                    .Take(SnippetsPerPage)
            )
                text.Append("> ").Append(Shorten(line.Trim(), 200)).Append('\n');
        }
        if (hits.Count > MaxSearchPages)
            text.Append(
                $"\n{hits.Count - MaxSearchPages} more pages match. Search for more words.\n"
            );
        return text.ToString();
    }

    /// <summary><c>skill_read</c>: one page from a character offset, cut to the result cap.</summary>
    public string Read(string role, string name, int offset)
    {
        var page = PagesFor(role).FirstOrDefault(p => p.Name == name.Trim());
        if (page is null)
            return $"There is no page named {name}. skills_list lists them.";
        offset = Math.Clamp(offset, 0, page.Text.Length);
        // Leave room for the note on where to go on.
        var cap = Math.Max(256, settings.Current.ResultCapBytes - 200);
        var length = page.Text.Length - offset;
        while (length > 0 && Encoding.UTF8.GetByteCount(page.Text.AsSpan(offset, length)) > cap)
            length -= Math.Max(1, length / 8);
        if (length > 0 && char.IsHighSurrogate(page.Text[offset + length - 1]))
            length--;
        var end = offset + length;
        return end >= page.Text.Length
            ? page.Text[offset..]
            : page.Text[offset..end]
                + $"\n[cut: characters {offset} to {end} of {page.Text.Length}. Read on with offset {end}.]";
    }

    private bool WithBook(string role) =>
        role == AssistantRoles.Admin || settings.Current.Player.QuasarBook;

    private string Folder(string role) =>
        Path.Combine(
            paths.DataDirectory,
            "skills",
            role == AssistantRoles.Player ? "player" : "admin"
        );

    private string FilePath(string role, string name) => Path.Combine(Folder(role), name + ".md");

    private static int Count(string text, string word)
    {
        var count = 0;
        for (
            var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            at >= 0;
            at = text.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase)
        )
            count++;
        return count;
    }

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";

    private static IReadOnlyList<SkillPage> ReadBook()
    {
        const string prefix = "QuasarAssistant.QuasarBook.";
        return typeof(SkillLibrary)
            .Assembly.GetManifestResourceNames()
            .Where(name =>
                name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".md")
            )
            .Select(name => new SkillPage(
                BookPrefix + name[prefix.Length..^3],
                ReadResource(name).ReplaceLineEndings("\n")
            ))
            .OrderBy(page => page.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(SkillLibrary).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();
}
