using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using QuasarSandbox;

// Inside the sandbox the server only compiles and runs one C# file.
if (args is ["--csharp", var source, ..])
    return await CSharpRunner.RunAsync(source, args[2..]);

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("The sandbox needs Linux with bubblewrap (bwrap).");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection("Sandbox"));
builder.Services.AddSingleton<Sandbox>();
builder.Services.AddSingleton<Mcp>();
builder.Services.AddHostedService<Cleanup>();
var options = builder.Configuration.GetSection("Sandbox").Get<SandboxOptions>() ?? new();
if (options.Token.Length < 16)
{
    Console.Error.WriteLine(
        "Set Sandbox:Token (16 characters or more), such as with Sandbox__Token."
    );
    return 1;
}
if (!Sandbox.CgroupsWork())
{
    if (options.RequireCgroups)
    {
        Console.Error.WriteLine(
            "systemd-run --user can't create a cgroup, so a run's memory and processes can't be limited. Run the "
                + "server under a user systemd session (loginctl enable-linger), or set Sandbox:RequireCgroups to false."
        );
        return 1;
    }
    Console.Error.WriteLine("Warning: no cgroup; only each process's address space is limited.");
}

// Input files arrive as JSON, base64 inflates them by a third.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = (long)options.DiskMb * 1024 * 1024 * 3 / 2 + 1024 * 1024
);
Directory.CreateDirectory(options.WorkRoot);
File.SetUnixFileMode(
    options.WorkRoot,
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
);

var app = builder.Build();
var expected = Encoding.UTF8.GetBytes("Bearer " + options.Token);
app.MapPost(
    "/mcp",
    (HttpRequest request, Mcp mcp, CancellationToken token) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()),
            expected
        )
            ? mcp.HandleAsync(request, token)
            : Task.FromResult(Results.Unauthorized())
);
await app.RunAsync();
return 0;

/// <summary>Deletes idle workspaces every few minutes.</summary>
internal sealed class Cleanup(Sandbox sandbox, ILogger<Cleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                sandbox.Clean();
            }
            catch (IOException error)
            {
                logger.LogWarning(error, "Workspace cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
