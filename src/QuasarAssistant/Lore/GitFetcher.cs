using System.Diagnostics;
using System.Text;

namespace QuasarAssistant.Lore;

/// <summary>What a fetch found: the commit (null for a local folder outside Git) and the Markdown files of the skill folder by relative path.</summary>
public sealed record GitSkill(string? Revision, IReadOnlyList<(string Path, string Text)> Files);

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

    public Task<GitSkill> FetchAsync(
        GitSkillUrl url,
        string? token,
        string workRoot,
        CancellationToken cancel
    ) =>
        CheckoutAsync(
            url.Repository,
            url.Ref,
            url.Path.Length > 0 ? [url.Path] : [],
            token,
            workRoot,
            (work, revision) => new GitSkill(revision, ReadFolder(work, url.Path, revision)),
            cancel
        );

    /// <summary>Checks out <paramref name="paths"/> (all of it when empty) of one commit into a scratch folder,
    /// hands the folder and the commit to <paramref name="read"/> and deletes the folder.</summary>
    public async Task<T> CheckoutAsync<T>(
        string repository,
        string gitRef,
        IReadOnlyList<string> paths,
        string? token,
        string workRoot,
        Func<string, string, T> read,
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
            await RunAsync(work, token, limit.Token, "remote", "add", "origin", repository);
            if (paths.Count > 0)
                await RunAsync(work, token, limit.Token, ["sparse-checkout", "set", .. paths]);
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
                gitRef.Length > 0 ? gitRef : "HEAD"
            );
            await RunAsync(work, token, limit.Token, "checkout", "-q", "FETCH_HEAD");
            var revision = (
                await RunAsync(work, token, limit.Token, "rev-parse", "FETCH_HEAD")
            ).Trim();
            return read(work, revision);
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

    /// <summary>The commit checked out in <paramref name="folder"/> when it is in a Git work tree, else null.</summary>
    public async Task<string?> LocalRevisionAsync(string folder, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            return (await RunAsync(folder, null, limit.Token, "rev-parse", "HEAD")).Trim();
        }
        catch (LoreFetchException) { }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        return null;
    }

    /// <summary>Reads a skill folder on the Quasar host, with the limits of a fetched one. The revision is the
    /// commit checked out there when the folder is in a Git work tree, else null.</summary>
    public async Task<GitSkill> ReadLocalAsync(string folder, CancellationToken cancel)
    {
        if (!Directory.Exists(folder))
            throw new LoreFetchException($"There is no folder {folder} on the Quasar host.");
        List<(string, string)> files;
        try
        {
            files = ReadFiles(folder, folder, folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LoreFetchException($"The folder {folder} could not be read: {error.Message}");
        }
        return new(await LocalRevisionAsync(folder, cancel), files);
    }

    private static List<(string, string)> ReadFolder(string work, string path, string revision)
    {
        var top = Path.GetFullPath(work);
        var root = Path.GetFullPath(Path.Combine(work, path));
        if (!root.StartsWith(top, StringComparison.Ordinal) || !Directory.Exists(root))
            throw new LoreFetchException(
                $"There is no folder {path} in the repository at {revision[..Math.Min(12, revision.Length)]}."
            );
        return ReadFiles(top, root, path.Length > 0 ? path : "/");
    }

    private static List<(string, string)> ReadFiles(string top, string root, string shown)
    {
        var gitFolder = Path.Combine(top, ".git");
        var files = Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(gitFolder, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            throw new LoreFetchException($"The folder {shown} has no Markdown (.md) files.");
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
