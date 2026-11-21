using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuasarAssistant.Lore;

namespace QuasarAssistant.Skills;

/// <summary>A file of a skill: <c>SKILL.md</c> or a support file next to it, by its path inside the skill folder.</summary>
public sealed record SkillFile(string Path, string Text);

/// <summary>One skill found in a source: the folder holding a <c>SKILL.md</c>, with the text files under it that no
/// deeper <c>SKILL.md</c> claims.</summary>
/// <param name="Folder">The skill folder in the source, such as <c>skills/octahedron-world</c>; empty for the root.</param>
public sealed record FoundSkill(
    string Name,
    string Folder,
    string Description,
    IReadOnlyList<SkillFile> Files
);

/// <summary>
/// Finds the skills in a tree of files, such as a ZIP or a checked-out repository folder: every <c>SKILL.md</c>
/// starts one, named by the <c>name:</c> of its front matter or else by its folder. Text support files under the
/// folder stay with it; binary files are left out, as the agents only read text.
/// </summary>
public static partial class SkillTree
{
    public const string SkillFileName = "SKILL.md";
    public const int MaxFiles = 500;
    public const int MaxFileBytes = 512 * 1024;
    public const int MaxTotalBytes = 4 * 1024 * 1024;
    public const int MaxZipBytes = 16 * 1024 * 1024;

    /// <summary>Finds the skills among <paramref name="files"/>, keyed by their path with <c>/</c> separators.
    /// <paramref name="fallbackName"/> names a <c>SKILL.md</c> at the root that has no <c>name:</c>.</summary>
    public static IReadOnlyList<FoundSkill> Find(
        IReadOnlyList<(string Path, byte[] Bytes)> files,
        string fallbackName
    )
    {
        var normalized = files
            .Select(file => (Path: Normalize(file.Path), file.Bytes))
            .Where(file => file.Path.Length > 0 && !Hidden(file.Path))
            .ToList();
        var folders = normalized
            .Where(file =>
                FileName(file.Path).Equals(SkillFileName, StringComparison.OrdinalIgnoreCase)
            )
            .Select(file => Parent(file.Path))
            .Distinct()
            .OrderBy(folder => folder, StringComparer.Ordinal)
            .ToList();
        if (folders.Count == 0)
            throw new LoreFetchException("No SKILL.md found.");
        var skills = new List<FoundSkill>();
        long total = 0;
        foreach (var folder in folders)
        {
            // Files under this folder that no deeper skill folder claims.
            var own = normalized
                .Where(file => Inside(file.Path, folder))
                .Where(file =>
                    !folders.Any(other =>
                        other.Length > folder.Length
                        && Inside(other, folder)
                        && Inside(file.Path, other)
                    )
                )
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
            var texts = new List<SkillFile>();
            foreach (var (path, bytes) in own)
            {
                if (bytes.Length > MaxFileBytes || !IsText(bytes))
                    continue;
                total += bytes.Length;
                if (total > MaxTotalBytes)
                    throw new LoreFetchException(
                        $"The skills' text files are over {MaxTotalBytes / 1024 / 1024} MiB together."
                    );
                var relative = folder.Length == 0 ? path : path[(folder.Length + 1)..];
                texts.Add(
                    new(
                        relative.Equals(SkillFileName, StringComparison.OrdinalIgnoreCase)
                            ? SkillFileName
                            : relative,
                        Encoding.UTF8.GetString(bytes).TrimStart('﻿').ReplaceLineEndings("\n")
                    )
                );
            }
            var main =
                texts.FirstOrDefault(file => file.Path == SkillFileName)
                ?? throw new LoreFetchException(
                    $"The SKILL.md in {Shown(folder)} is not a UTF-8 text file under {MaxFileBytes / 1024} KiB."
                );
            var front = FrontMatter(main.Text);
            var name =
                front.GetValueOrDefault("name") is { } named && SkillLibrary.IsValidName(named)
                    ? named
                    : Slug(folder.Length == 0 ? fallbackName : FileName(folder));
            if (!SkillLibrary.IsValidName(name))
                throw new LoreFetchException(
                    $"The skill in {Shown(folder)} has no usable name; give its SKILL.md a name: line of letters, digits, - or _."
                );
            skills.Add(new(name, folder, front.GetValueOrDefault("description") ?? "", texts));
        }
        var twice = skills.GroupBy(skill => skill.Name).FirstOrDefault(group => group.Count() > 1);
        if (twice is not null)
            throw new LoreFetchException(
                $"Two skills are named {twice.Key}: in {string.Join(" and ", twice.Select(skill => Shown(skill.Folder)))}."
            );
        return skills;
    }

    /// <summary>The files of a ZIP archive, folders left out. Throws for a broken or too large archive.</summary>
    public static IReadOnlyList<(string Path, byte[] Bytes)> ReadZip(byte[] zip)
    {
        if (zip.Length > MaxZipBytes)
            throw new LoreFetchException($"The ZIP is over {MaxZipBytes / 1024 / 1024} MiB.");
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
            if (entries.Count > MaxFiles)
                throw new LoreFetchException(
                    $"The ZIP has {entries.Count} files; at most {MaxFiles} are read."
                );
            var files = new List<(string, byte[])>();
            foreach (var entry in entries)
            {
                // Never more than the limit from an entry, whatever its header claims.
                using var stream = entry.Open();
                var buffer = new byte[MaxFileBytes + 1];
                var length = 0;
                int read;
                while (
                    length < buffer.Length
                    && (read = stream.Read(buffer, length, buffer.Length - length)) > 0
                )
                    length += read;
                // A file over the limit is left out like a binary one.
                files.Add((entry.FullName, length > MaxFileBytes ? [0] : buffer[..length]));
            }
            return files;
        }
        catch (InvalidDataException error)
        {
            throw new LoreFetchException("The file is not a valid ZIP: " + error.Message);
        }
    }

    /// <summary>The files under <paramref name="root"/>, by path relative to it; a file over the size limit reads
    /// as binary.</summary>
    public static IReadOnlyList<(string Path, byte[] Bytes)> ReadFolder(string root)
    {
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (Full: file, Path: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(file => !Hidden(file.Path))
            .ToList();
        if (files.Count > MaxFiles)
            throw new LoreFetchException(
                $"The folder has {files.Count} files; at most {MaxFiles} are read."
            );
        return files
            .Select(file =>
                (
                    file.Path,
                    new FileInfo(file.Full).Length > MaxFileBytes
                        ? new byte[] { 0 }
                        : File.ReadAllBytes(file.Full)
                )
            )
            .ToList();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The <c>key: value</c> lines of a YAML front matter block, without quotes.</summary>
    public static IReadOnlyDictionary<string, string> FrontMatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        if (lines[0].Trim() != "---")
            return values;
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim() == "---")
                break;
            var match = FrontLine().Match(line);
            if (match.Success)
                values.TryAdd(match.Groups[1].Value, match.Groups[2].Value.Trim().Trim('"', '\''));
        }
        return values;
    }

    private static bool IsText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return false;
        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Shown(string folder) => folder.Length == 0 ? "the root" : folder;

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    // Dot folders and files such as .git and .DS_Store, and the __MACOSX copies in ZIPs made on a Mac.
    private static bool Hidden(string path) =>
        path.Split('/').Any(part => part.StartsWith('.') || part == "__MACOSX");

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Parent(string path) =>
        path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    private static bool Inside(string path, string folder) =>
        folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static string Slug(string name)
    {
        var slug = SlugChars().Replace(name.Trim(), "-").Trim('-');
        return slug.Length > 64 ? slug[..64] : slug;
    }

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s*:\s*(.*)$")]
    private static partial Regex FrontLine();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex SlugChars();
}
