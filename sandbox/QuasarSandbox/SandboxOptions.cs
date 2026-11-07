namespace QuasarSandbox;

/// <summary>The <c>Sandbox</c> configuration section. Callers choose a timeout up to the maximum; every other
/// limit is the server's.</summary>
public sealed class SandboxOptions
{
    /// <summary>Clients send it as <c>Authorization: Bearer &lt;token&gt;</c>. The server doesn't start without one.</summary>
    public string Token { get; set; } = "";

    /// <summary>Where the workspaces live, one folder per conversation.</summary>
    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "quasar-sandbox");

    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>The assistant plugin ends an MCP call after 120 s, so a run should end well before that.</summary>
    public int MaxTimeoutSeconds { get; set; } = 90;

    /// <summary>CPU of a run in percent of one core, where a cgroup limits it.</summary>
    public int CpuPercent { get; set; } = 100;

    /// <summary>Memory of a run: a cgroup limit where <c>systemd-run --user</c> works, and an address-space limit
    /// per process.</summary>
    public int MemoryMb { get; set; } = 512;

    /// <summary>Size of the workspace: the run's working folder is a tmpfs of this size.</summary>
    public int DiskMb { get; set; } = 64;

    /// <summary>Kept of stdout and of stderr each; the rest is cut.</summary>
    public int OutputKb { get; set; } = 64;

    /// <summary>Processes and threads of a run, where a cgroup limits it.</summary>
    public int MaxTasks { get; set; } = 64;

    public int MaxParallelRuns { get; set; } = 2;

    /// <summary>A workspace unused this long is deleted.</summary>
    public int IdleMinutes { get; set; } = 24 * 60;

    /// <summary>Without a cgroup only the address space of each process is limited, not their number or sum, so
    /// the server refuses to start unless this is turned off.</summary>
    public bool RequireCgroups { get; set; } = true;

    public string Python { get; set; } = "/usr/bin/python3";

    /// <summary>Host folders bound read-only into the sandbox at the same path, besides <c>/usr</c>, the .NET
    /// runtime and this server's own folder. For a Python or libraries installed elsewhere.</summary>
    public List<string> ReadOnlyPaths { get; set; } = [];
}
