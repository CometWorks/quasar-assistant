using System.Diagnostics;
using System.Text;

namespace QuasarAssistant.Lore;

/// <summary>What a fetch found: the commit and the Markdown files of the skill folder by relative path.</summary>
public sealed record GitSkill(string Revision, IReadOnlyList<(string Path, string Text)> Files);

/// <summary>
/// Fetches one folder of a Git repository with the <c>git</c> command: a shallow, partial fetch of one commit with
/// a sparse checkout, so only the skill folder's files are downloaded. The token goes in an HTTP header through
/// the environment, never on the command line, and is cut from any error text.
/// </summary>
public sealed class GitFetcher
{
    public const int MaxFiles = 100;
    public const int MaxBytes = 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public async Task<GitSkill> FetchAsync(
        GitSkillUrl url,
        string? token,
        string workRoot,
        CancellationToken cancel
    )
    {
        var work = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            await RunAsync(work, token, limit.Token, "init", "-q");
            await RunAsync(work, token, limit.Token, "remote", "add", "origin", url.Repository);
            if (url.Path.Length > 0)
                await RunAsync(work, token, limit.Token, "sparse-checkout", "set", url.Path);
            await RunAsync(
                work,
                token,
                limit.Token,
                "fetch",
                "-q",
                "--depth",
                "1",
                "--filter=blob:none",
                "origin",
                url.Ref.Length > 0 ? url.Ref : "HEAD"
            );
            await RunAsync(work, token, limit.Token, "checkout", "-q", "FETCH_HEAD");
            var revision = (
                await RunAsync(work, token, limit.Token, "rev-parse", "FETCH_HEAD")
            ).Trim();
            return new(revision, ReadFolder(work, url.Path, revision));
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LoreFetchException($"git took longer than {Timeout.TotalSeconds:0} s.");
        }
        finally
        {
            Delete(work);
        }
    }

    private static List<(string, string)> ReadFolder(string work, string path, string revision)
    {
        var top = Path.GetFullPath(work);
        var root = Path.GetFullPath(Path.Combine(work, path));
        if (!root.StartsWith(top, StringComparison.Ordinal) || !Directory.Exists(root))
            throw new LoreFetchException(
                $"There is no folder {path} in the repository at {revision[..Math.Min(12, revision.Length)]}."
            );
        var gitFolder = Path.Combine(top, ".git");
        var files = Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(gitFolder, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            throw new LoreFetchException(
                $"The folder {(path.Length > 0 ? path : "/")} has no Markdown (.md) files."
            );
        if (files.Count > MaxFiles)
            throw new LoreFetchException(
                $"The skill has {files.Count} Markdown files; at most {MaxFiles} are read."
            );
        List<(string, string)> read = [];
        long bytes = 0;
        foreach (var file in files)
        {
            bytes += new FileInfo(file).Length;
            if (bytes > MaxBytes)
                throw new LoreFetchException(
                    $"The skill's Markdown files are over {MaxBytes / 1024} KiB together."
                );
            read.Add(
                (
                    Path.GetRelativePath(root, file).Replace('\\', '/'),
                    File.ReadAllText(file).ReplaceLineEndings("\n")
                )
            );
        }
        return read;
    }

    private static async Task<string> RunAsync(
        string work,
        string? token,
        CancellationToken cancel,
        params string[] arguments
    )
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // Never ask for credentials, and no system credential helper.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        List<(string Key, string Value)> config = [("credential.helper", "")];
        if (!string.IsNullOrEmpty(token))
            config.Add(
                (
                    "http.extraHeader",
                    "Authorization: Basic "
                        + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token))
                )
            );
        start.Environment["GIT_CONFIG_COUNT"] = config.Count.ToString();
        for (var i = 0; i < config.Count; i++)
        {
            start.Environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            start.Environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new LoreFetchException("git is not installed on the Quasar host.");
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var errors = process.StandardError.ReadToEndAsync(cancel);
            try
            {
                await process.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode == 0)
                return await output;
            var message = (await errors).Trim();
            if (!string.IsNullOrEmpty(token))
                message = message.Replace(token, "<redacted>");
            var last = message.Split('\n').LastOrDefault(line => line.Trim().Length > 0)?.Trim();
            // Hosts answer a missing and a private repository alike, by asking for credentials.
            if (last?.Contains("could not read Username", StringComparison.Ordinal) == true)
                last = "the repository was not found, or it is private and needs an access token";
            throw new LoreFetchException(
                $"git {arguments[0]} failed: {(string.IsNullOrEmpty(last) ? "exit code " + process.ExitCode : last)}"
            );
        }
    }

    private static void Delete(string folder)
    {
        try
        {
            // Git makes its pack files read-only, which stops a delete on Windows.
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class LoreFetchException(string message) : Exception(message);
