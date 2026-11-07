using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace QuasarSandbox;

/// <summary>
/// Compiles one C# file and runs it in this process. The server starts itself this way inside the sandbox
/// (<c>--csharp main.cs</c>), so user code never runs in the server's own process.
/// </summary>
public static class CSharpRunner
{
    private const string Usings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Text;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public static async Task<int> RunAsync(string path, string[] args)
    {
        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "main",
            [
                CSharpSyntaxTree.ParseText(Usings, parse),
                CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path), parse, "main.cs"),
            ],
            References(),
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            foreach (
                var error in emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            )
                await Console.Error.WriteLineAsync(error.ToString());
            await Console.Error.WriteLineAsync("Compilation failed.");
            return 1;
        }
        image.Position = 0;
        var entry = AssemblyLoadContext.Default.LoadFromStream(image).EntryPoint!;
        object? result;
        try
        {
            result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [args]);
            if (result is Task task)
            {
                await task;
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
        }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            await Console.Error.WriteLineAsync(inner.ToString());
            return 1;
        }
        await Console.Out.FlushAsync();
        return result as int? ?? Environment.ExitCode;
    }

    // The base library of the running .NET, as a console app gets it. No NuGet packages.
    private static IEnumerable<MetadataReference> References() =>
        Directory
            .EnumerateFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
            .Where(file =>
                Path.GetFileName(file) is var name
                && (
                    name.StartsWith("System.", StringComparison.Ordinal)
                    || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
                    || name is "mscorlib.dll" or "netstandard.dll" or "Microsoft.CSharp.dll"
                )
            )
            .Select(file => MetadataReference.CreateFromFile(file));
}
