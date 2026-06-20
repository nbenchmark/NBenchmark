using System.Diagnostics;
using System.Reflection;

namespace NBenchmark.Engine;

internal static class MultiRuntimeOrchestrator
{
    public static async Task<IReadOnlyList<TfmBuild>> BuildForRuntimesAsync(
        IReadOnlyList<RuntimeMoniker> runtimes,
        CancellationToken cancellationToken)
    {
        var projectRoot = FindProjectRoot();
        var results = new List<TfmBuild>();

        foreach (var moniker in runtimes)
        {
            var tfm = moniker.ToTargetFramework();

            var outputDir = Path.Combine(
                Path.GetTempPath(),
                $"nbench-rt-{tfm}-{Guid.NewGuid():N}");

            Console.WriteLine($"  Building {tfm}...");

            var (exitCode, errorOutput) = await RunDotnetBuildAsync(tfm, outputDir, projectRoot, cancellationToken)
                .ConfigureAwait(false);

            if (exitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(errorOutput)
                    ? ""
                    : $"\n{errorOutput.Trim()}";

                TryDeleteBuildOutput(outputDir);
                results.Add(new TfmBuild(moniker, null, null, $"Build failed for {tfm} (exit code {exitCode}){detail}"));
                continue;
            }

            var dllPath = FindEntryAssemblyDll(outputDir);

            if (dllPath is null)
            {
                TryDeleteBuildOutput(outputDir);
                results.Add(new TfmBuild(moniker, null, null, $"Could not locate entry assembly DLL in {outputDir}"));
                continue;
            }

            results.Add(new TfmBuild(moniker, dllPath, outputDir, null));
        }

        return results;
    }

    private static async Task<(int ExitCode, string ErrorOutput)> RunDotnetBuildAsync(
        string tfm,
        string outputDir,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(tfm);
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stderr);
    }

    private static string? FindEntryAssemblyDll(string outputDir)
    {
        // The entry assembly's name is known at runtime; prefer it over any other
        // deps.json in the output directory (referenced libraries also produce one).
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;

        if (!string.IsNullOrEmpty(entryName))
        {
            var preferredDll = Path.Combine(outputDir, entryName + ".dll");

            if (File.Exists(preferredDll))
                return preferredDll;
        }

        // Fallback: find the deps.json whose name matches a DLL in the output directory.
        // The deps.json file is generated only for the entry assembly of each project.
        var depsFiles = Directory.GetFiles(outputDir, "*.deps.json");

        foreach (var deps in depsFiles)
        {
            var depsBaseName = Path.GetFileNameWithoutExtension(deps);

            var assemblyName = depsBaseName.EndsWith(".deps", StringComparison.Ordinal)
                ? depsBaseName[..^5]
                : depsBaseName;

            var dllPath = Path.Combine(outputDir, assemblyName + ".dll");

            if (File.Exists(dllPath))
                return dllPath;
        }

        // Last resort: first non-framework DLL in the output directory.
        return Directory.GetFiles(outputDir, "*.dll")
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);

                return !name.StartsWith("System.", StringComparison.Ordinal)
                       && !name.StartsWith("Microsoft.", StringComparison.Ordinal)
                       && !name.StartsWith("mscorlib", StringComparison.Ordinal)
                       && !string.Equals(name, "netstandard", StringComparison.Ordinal);
            });
    }

    private static string FindProjectRoot()
    {
        // Prefer the entry assembly's directory: when `dotnet run --project samples/Foo`
        // executes, the current working directory is the repo root, but the entry assembly
        // lives in the project's build output. Walk up from the entry assembly to find its
        // .csproj so `dotnet build -f <tfm>` targets a single project, not the whole solution.
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;

        if (!string.IsNullOrEmpty(entryAssembly))
        {
            var dir = Path.GetDirectoryName(entryAssembly);

            while (dir is not null)
            {
                if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                    return dir;

                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        // Fall back to CWD-based lookup.
        var csprojDir = Environment.CurrentDirectory;

        while (csprojDir is not null)
        {
            if (Directory.GetFiles(csprojDir, "*.csproj").Length > 0)
                return csprojDir;

            csprojDir = Directory.GetParent(csprojDir)?.FullName;
        }

        var slnDir = Environment.CurrentDirectory;

        while (slnDir is not null)
        {
            if (Directory.GetFiles(slnDir, "*.sln").Length > 0)
                return slnDir;

            slnDir = Directory.GetParent(slnDir)?.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate a .csproj or .sln file. Run the benchmark from within a project directory.");
    }

    public static void TryDeleteBuildOutput(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            return;

        try
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
        catch
        {
            // Best-effort cleanup of temporary runtime build output.
        }
    }
}

internal sealed record TfmBuild(RuntimeMoniker Moniker, string? DllPath, string? OutputDirectory, string? Error);
