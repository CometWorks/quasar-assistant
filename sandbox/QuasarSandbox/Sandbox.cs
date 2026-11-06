using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace QuasarSandbox;

public sealed record InputFile(string Path, string Content, bool Base64 = false);

public sealed record FileChange(string Path, long Size);

/// <param name="ExitCode">Null when the run was killed from outside, such as at the hard deadline.</param>
public sealed record RunOutcome(
    int? ExitCode,
    bool TimedOut,
    string Stdout,
    bool StdoutCut,
    string Stderr,
    bool StderrCut,
    double Seconds,
    IReadOnlyList<FileChange> Changed,
    IReadOnlyList<FileChange> Files
);

public sealed class SandboxException(string message) : Exception(message);

/// <summary>
/// Runs code in a bubblewrap sandbox: every namespace unshared (no network, own PIDs), <c>/usr</c> and the .NET
/// runtime read-only, the workspace copied into a tmpfs of the disk limit, memory capped per process and, where
/// <c>systemd-run --user</c> works, by a cgroup for the whole run. The workspace goes back out as a tar stream on
/// stdout, which the server reads with a size cap and unpacks as plain files only.
/// </summary>
public sealed class Sandbox(IOptions<SandboxOptions> options, ILogger<Sandbox> logger)
{
    public const string ConversationMeta = "quasar.assistant/conversation";

    /// <summary>Added to the memory limit of a C# run for Roslyn.</summary>
    public const int CompilerMb = 256;

    private readonly SandboxOptions _options = options.Value;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxParallelRuns));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly Lazy<bool> _cgroups = new(CgroupsWork);

    public int ClampTimeout(int? seconds) =>
        Math.Clamp(seconds ?? _options.DefaultTimeoutSeconds, 1, _options.MaxTimeoutSeconds);

    /// <summary>The workspace folder of a conversation; a hash, so the key can't name a path.</summary>
    public string WorkspaceOf(string conversation) =>
        Path.Combine(
            _options.WorkRoot,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conversation)))[..32]
        );

    public async Task<RunOutcome> RunAsync(
        string? conversation,
        string language,
        string code,
        IReadOnlyList<InputFile> files,
        int timeoutSeconds,
        CancellationToken token
    )
    {
        if (language is not ("python" or "csharp"))
            throw new SandboxException("The language must be python or csharp.");
        var oneOff = conversation is null;
        var workspace = WorkspaceOf(conversation ?? Guid.NewGuid().ToString("N"));
        var gate = _locks.GetOrAdd(workspace, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            await _slots.WaitAsync(token);
            try
            {
                return await RunInAsync(workspace, language, code, files, timeoutSeconds, token);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            gate.Release();
            if (oneOff)
                Delete(workspace);
        }
    }

    /// <summary>A file of the workspace, from the offset, at most the length.</summary>
    public async Task<(byte[] Data, long Size)> ReadAsync(
        string conversation,
        string path,
        long offset,
        int length,
        CancellationToken token
    )
    {
        var full = Resolve(Path.Combine(WorkspaceOf(conversation), "files"), path);
        if (!File.Exists(full))
            throw new SandboxException($"There is no file {path} in the workspace.");
        await using var file = File.OpenRead(full);
        file.Position = Math.Clamp(offset, 0, file.Length);
        var buffer = new byte[Math.Clamp(length, 0, 1024 * 1024)];
        var read = await file.ReadAtLeastAsync(buffer, buffer.Length, false, token);
        return (buffer[..read], file.Length);
    }

    /// <summary>Deletes the workspaces not used for the idle time.</summary>
    public void Clean()
    {
        if (!Directory.Exists(_options.WorkRoot))
            return;
        var before = DateTime.UtcNow.AddMinutes(-_options.IdleMinutes);
        foreach (var workspace in Directory.EnumerateDirectories(_options.WorkRoot))
            if (
                Directory.GetLastWriteTimeUtc(workspace) < before
                && _locks.GetOrAdd(workspace, _ => new SemaphoreSlim(1)).Wait(0)
            )
                try
                {
                    Delete(workspace);
                }
                finally
                {
                    _locks.TryRemove(workspace, out _);
                }
    }

    private async Task<RunOutcome> RunInAsync(
        string workspace,
        string language,
        string code,
        IReadOnlyList<InputFile> inputs,
        int timeoutSeconds,
        CancellationToken token
    )
    {
        var files = Path.Combine(workspace, "files");
        var codeDir = Path.Combine(workspace, "code");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(codeDir);
        Directory.SetLastWriteTimeUtc(workspace, DateTime.UtcNow);
        var diskBytes = (long)_options.DiskMb * 1024 * 1024;
        foreach (var input in inputs)
        {
            var target = Resolve(files, input.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] bytes;
            try
            {
                bytes = input.Base64
                    ? Convert.FromBase64String(input.Content)
                    : Encoding.UTF8.GetBytes(input.Content);
            }
            catch (FormatException)
            {
                throw new SandboxException($"The content of {input.Path} isn't valid base64.");
            }
            await File.WriteAllBytesAsync(target, bytes, token);
        }
        var before = List(files);
        var beforeTimes = before
            .Select(file => (file.Path, file.Size, Time(files, file)))
            .ToHashSet();
        if (before.Sum(file => file.Size) > diskBytes)
            throw new SandboxException(
                $"The workspace holds more than its {_options.DiskMb} MB. Use smaller files."
            );
        var source = Path.Combine(codeDir, language == "python" ? "main.py" : "main.cs");
        await File.WriteAllTextAsync(source, code, token);

        var clock = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = StartInfo(workspace, language, timeoutSeconds),
        };
        process.Start();
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(token);
        // The run's own timeout, then time to copy the workspace out.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 30));
        var next = Path.Combine(workspace, "next");
        Delete(next);
        Directory.CreateDirectory(next);
        var outputCap = _options.OutputKb * 1024;
        byte[] stdout = [],
            stderr = [];
        int? exit = null;
        try
        {
            await using var reader = new TarReader(process.StandardOutput.BaseStream);
            long total = 0;
            while (await reader.GetNextEntryAsync(copyData: false, deadline.Token) is { } entry)
            {
                total += entry.Length;
                if (total > diskBytes + 2L * outputCap + 4096)
                    throw new SandboxException("The run produced more than its limits allow.");
                var name = entry.Name.TrimEnd('/');
                if (name.StartsWith("work/", StringComparison.Ordinal))
                {
                    var target = Resolve(next, name["work/".Length..]);
                    if (entry.EntryType == TarEntryType.Directory)
                        Directory.CreateDirectory(target);
                    else if (
                        entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    )
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using (var file = File.Create(target))
                            if (entry.DataStream is { } data)
                                await data.CopyToAsync(file, deadline.Token);
                        File.SetLastWriteTimeUtc(target, entry.ModificationTime.UtcDateTime);
                    }
                    // Links, devices and anything else stay in the sandbox.
                }
                else if (
                    name is "out/stdout" or "err/stderr" or "meta/exit"
                    && entry.DataStream is { } data
                )
                {
                    using var copy = new MemoryStream();
                    await data.CopyToAsync(copy, deadline.Token);
                    var bytes = copy.ToArray();
                    if (name == "out/stdout")
                        stdout = bytes;
                    else if (name == "err/stderr")
                        stderr = bytes;
                    else if (int.TryParse(Encoding.ASCII.GetString(bytes).Trim(), out var code2))
                        exit = code2;
                }
            }
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Delete(next);
            return new(
                null,
                true,
                "",
                false,
                "The run passed its hard deadline and was killed; the workspace is unchanged.",
                false,
                clock.Elapsed.TotalSeconds,
                [],
                before
            );
        }
        catch (Exception error)
            when (error is InvalidDataException or SandboxException or EndOfStreamException)
        {
            process.Kill(entireProcessTree: true);
            Delete(next);
            throw new SandboxException("The sandbox failed: " + error.Message);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        if (exit is null)
        {
            Delete(next);
            var reason = (await errors).Trim();
            logger.LogWarning("Sandbox run failed: {Error}", reason);
            throw new SandboxException(
                "The sandbox failed: "
                    + (reason.Length > 0 ? reason : $"exit code {process.ExitCode}")
            );
        }
        Delete(files);
        Directory.Move(next, files);
        var after = List(files);
        var changed = after
            .Where(file => !beforeTimes.Contains((file.Path, file.Size, Time(files, file))))
            .ToList();
        // timeout exits with 124 when it stopped the code.
        var timedOut = exit == 124 && clock.Elapsed.TotalSeconds >= timeoutSeconds - 0.5;
        return new(
            timedOut ? null : exit,
            timedOut,
            Text(stdout, outputCap, out var stdoutCut),
            stdoutCut,
            Text(stderr, outputCap, out var stderrCut),
            stderrCut,
            clock.Elapsed.TotalSeconds,
            changed,
            after
        );
    }

    private ProcessStartInfo StartInfo(string workspace, string language, int timeoutSeconds)
    {
        // C# compiles in the sandbox, in the same process as the code: the compiler gets its own allowance.
        var memoryMb = _options.MemoryMb + (language == "csharp" ? CompilerMb : 0);
        var memoryKb = (long)memoryMb * 1024;
        // The server's folder and a .NET outside /usr go to fixed places, so no host path shows inside.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var hostDotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var dotnetRoot = hostDotnet.StartsWith("/usr/", StringComparison.Ordinal)
            ? hostDotnet
            : "/opt/dotnet";
        const string self = "/opt/quasar-sandbox";
        string command;
        if (language == "python")
            command = $"ulimit -v {memoryKb}; exec {Quote(_options.Python)} -I -B /code/main.py";
        else
            // The runtime reserves address space well beyond what it uses; the GC limit caps the heap.
            command =
                $"ulimit -v {memoryKb + 8L * 1024 * 1024}; exec {Quote(Path.Combine(dotnetRoot, "dotnet"))} {Quote(Path.Combine(self, "QuasarSandbox.dll"))} --csharp /code/main.cs";
        var outputCap = _options.OutputKb * 1024;
        var script =
            "cp -a /in/. /work/ || exit 90\n"
            + "cd /work\n"
            + $"( timeout -k 2 {timeoutSeconds} sh -c {Quote(command)} >/out/stdout 2>/err/stderr </dev/null )\n"
            + "echo $? >/meta/exit\n"
            + "cd / && exec tar -c -f - work out err meta\n";

        var info = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        List<string> args = [];
        string fileName;
        if (_cgroups.Value)
        {
            fileName = "systemd-run";
            args.AddRange([
                "--user",
                "--scope",
                "--quiet",
                "-p",
                // The tmpfs files count against the cgroup too.
                $"MemoryMax={memoryMb + _options.DiskMb + 2 * _options.OutputKb / 1024 + 64}M",
                "-p",
                "MemorySwapMax=0",
                "-p",
                $"TasksMax={_options.MaxTasks}",
                "-p",
                $"CPUQuota={_options.CpuPercent}%",
                "--",
                "bwrap",
            ]);
        }
        else
            fileName = "bwrap";
        args.AddRange([
            "--unshare-all",
            "--die-with-parent",
            "--new-session",
            "--cap-drop",
            "ALL",
            "--ro-bind",
            "/usr",
            "/usr",
            "--symlink",
            "usr/lib",
            "/lib",
            "--symlink",
            "usr/lib64",
            "/lib64",
            "--symlink",
            "usr/bin",
            "/bin",
            "--proc",
            "/proc",
            "--dev",
            "/dev",
            "--size",
            (16L * 1024 * 1024).ToString(),
            "--tmpfs",
            "/tmp",
            "--size",
            ((long)_options.DiskMb * 1024 * 1024).ToString(),
            "--tmpfs",
            "/work",
            "--size",
            (outputCap + 4096L).ToString(),
            "--tmpfs",
            "/out",
            "--size",
            (outputCap + 4096L).ToString(),
            "--tmpfs",
            "/err",
            "--size",
            "65536",
            "--tmpfs",
            "/meta",
            "--ro-bind",
            Path.Combine(workspace, "files"),
            "/in",
            "--ro-bind",
            Path.Combine(workspace, "code"),
            "/code",
        ]);
        args.AddRange(["--ro-bind", AppContext.BaseDirectory.TrimEnd('/'), self]);
        if (dotnetRoot != hostDotnet)
            args.AddRange(["--ro-bind", hostDotnet, dotnetRoot]);
        foreach (var path in _options.ReadOnlyPaths.Distinct())
            args.AddRange(["--ro-bind", path, path]);
        args.AddRange([
            "--chdir",
            "/work",
            "--clearenv",
            "--setenv",
            "PATH",
            "/usr/local/bin:/usr/bin:/bin",
            "--setenv",
            "HOME",
            "/tmp",
            "--setenv",
            "TMPDIR",
            "/tmp",
            "--setenv",
            "LANG",
            "C.UTF-8",
            "--setenv",
            "DOTNET_ROOT",
            dotnetRoot,
            "--setenv",
            "DOTNET_CLI_TELEMETRY_OPTOUT",
            "1",
            "--setenv",
            "DOTNET_gcServer",
            "0",
            "--setenv",
            "DOTNET_GCHeapHardLimit",
            $"0x{(long)memoryMb * 1024 * 1024 * 3 / 4:X}",
            "--setenv",
            "PYTHONDONTWRITEBYTECODE",
            "1",
            "/bin/sh",
            "-c",
            script,
        ]);
        info.FileName = fileName;
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>Whether <c>systemd-run --user</c> can put a run in a cgroup with limits.</summary>
    public static bool CgroupsWork()
    {
        try
        {
            using var probe = Process.Start(
                new ProcessStartInfo(
                    "systemd-run",
                    ["--user", "--scope", "--quiet", "-p", "MemoryMax=64M", "true"]
                )
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }
            )!;
            return probe.WaitForExit(10_000) && probe.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string Resolve(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path.TrimStart('/')));
        if (
            path.Length == 0
            || !full.StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
        )
            throw new SandboxException($"The path {path} is outside the workspace.");
        return full;
    }

    private static List<FileChange> List(string root) =>
        Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => new FileChange(
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                new FileInfo(file).Length
            ))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

    // Whole seconds: the tar stream keeps no more.
    private static long Time(string root, FileChange file) =>
        File.GetLastWriteTimeUtc(Path.Combine(root, file.Path)).Ticks / TimeSpan.TicksPerSecond;

    private static string Text(byte[] bytes, int cap, out bool cut)
    {
        cut = bytes.Length >= cap;
        return Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, cap));
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
