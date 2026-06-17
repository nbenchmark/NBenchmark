using System.Diagnostics;
using System.Reflection;
using NBenchmark;
using NBenchmark.Discovery;
using NBenchmark.Reporters.Console;

var (projectPaths, assemblyPaths, remainingArgs) = ParseArgs(args);

if (Environment.ExitCode != 0)
    return;

if (remainingArgs.Contains("--help") || remainingArgs.Contains("-h"))
{
    PrintToolHelp();
    return;
}

var assemblies = new List<Assembly>();

if (IsIsolatedChildProcess())
    assemblyPaths.AddRange(ReadForwardedAssemblyPaths());

foreach (var path in projectPaths)
{
    var dllPath = BuildProject(path);

    if (dllPath is null)
        return;

    try
    {
        assemblies.Add(Assembly.LoadFrom(dllPath));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error loading built assembly '{dllPath}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

foreach (var path in assemblyPaths)
{
    try
    {
        assemblies.Add(Assembly.LoadFrom(path));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error loading assembly '{path}': {ex.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

if (projectPaths.Count == 0 && assemblyPaths.Count == 0)
{
    var discoverer = new BenchmarkDiscoverer();

    foreach (var dll in Directory.EnumerateFiles(Environment.CurrentDirectory, "*.dll"))
    {
        try
        {
            var asm = Assembly.LoadFrom(dll);

            if (discoverer.Discover(asm).Count > 0)
                assemblies.Add(asm);
        }
        catch
        {
        }
    }
}

if (assemblies.Count == 0)
{
    Console.Error.WriteLine("No benchmark assemblies found. Build your project first, or use --project <path>.");
    Environment.ExitCode = 1;
    return;
}

// Force load of the console reporter assembly before CLI parsing resolves reporter names.
_ = typeof(ConsoleReporter);

var host = BenchmarkHost.Create([.. remainingArgs]);

if (!HasReporterFlag(remainingArgs))
    host.WithReporter(new ConsoleReporter());

foreach (var asm in assemblies)
    host.AddFromAssembly(asm);

var benchmarkAssemblyPaths = assemblies
    .Select(a => a.Location)
    .Where(static p => !string.IsNullOrWhiteSpace(p))
    .Select(Path.GetFullPath)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

var previousForwarded = Environment.GetEnvironmentVariable("NBENCHMARK_TOOL_ASSEMBLIES");
Environment.SetEnvironmentVariable(
    "NBENCHMARK_TOOL_ASSEMBLIES",
    string.Join(Path.PathSeparator, benchmarkAssemblyPaths));

try
{
    await host.RunAsync();
}
finally
{
    Environment.SetEnvironmentVariable("NBENCHMARK_TOOL_ASSEMBLIES", previousForwarded);
}

return;

static (List<string> projectPaths, List<string> assemblyPaths, List<string> remaining) ParseArgs(string[] args)
{
    var projectPaths = new List<string>();
    var assemblyPaths = new List<string>();
    var remaining = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--project")
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing value for '--project'.");
                Environment.ExitCode = 1;
                return (projectPaths, assemblyPaths, remaining);
            }

            projectPaths.Add(args[++i]);
        }
        else if (args[i] == "--assembly")
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing value for '--assembly'.");
                Environment.ExitCode = 1;
                return (projectPaths, assemblyPaths, remaining);
            }

            assemblyPaths.Add(args[++i]);
        }
        else
            remaining.Add(args[i]);
    }

    return (projectPaths, assemblyPaths, remaining);
}

static bool IsIsolatedChildProcess() =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NBENCHMARK_ISOLATED_REQUEST_PATH"));

static List<string> ReadForwardedAssemblyPaths()
{
    var raw = Environment.GetEnvironmentVariable("NBENCHMARK_TOOL_ASSEMBLIES");

    if (string.IsNullOrWhiteSpace(raw))
        return [];

    return raw
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}

static string? BuildProject(string projectPath)
{
    if (!File.Exists(projectPath))
    {
        if (Directory.Exists(projectPath))
        {
            var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly);

            if (csprojFiles.Length == 0)
            {
                Console.Error.WriteLine($"No .csproj found in directory '{projectPath}'.");
                Environment.ExitCode = 1;
                return null;
            }

            if (csprojFiles.Length > 1)
            {
                Console.Error.WriteLine($"Multiple .csproj files found in directory '{projectPath}'. Pass an explicit project path:");

                foreach (var file in csprojFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"  - {Path.GetFileName(file)}");

                Environment.ExitCode = 1;
                return null;
            }

            projectPath = csprojFiles[0];
        }
        else
        {
            Console.Error.WriteLine($"Project path not found: '{projectPath}'.");
            Environment.ExitCode = 1;
            return null;
        }
    }

    var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
    var projectName = Path.GetFileNameWithoutExtension(projectPath);

    Console.WriteLine($"Building '{projectName}'...");

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        ArgumentList = { "build", "-c", "Release", projectPath },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    using var process = Process.Start(psi);

    if (process is null)
    {
        Console.Error.WriteLine("Failed to start dotnet build.");
        Environment.ExitCode = 1;
        return null;
    }

    var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
    var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

    process.WaitForExit();
    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();

    if (process.ExitCode != 0)
    {
        Console.Write(stdout);

        if (stderr.Length > 0)
            Console.Error.Write(stderr);

        Environment.ExitCode = 1;
        return null;
    }

    if (stderr.Length > 0)
        Console.Error.Write(stderr);

    var releaseDir = Path.Combine(projectDir, "bin", "Release");

    if (Directory.Exists(releaseDir))
    {
        var dlls = Directory.GetFiles(releaseDir, $"{projectName}.dll", SearchOption.AllDirectories).ToList();

        if (dlls.Count > 0)
            return SelectBestBuildOutput(dlls);
    }

    Console.Error.WriteLine($"Could not find build output for '{projectName}'.");
    Environment.ExitCode = 1;
    return null;
}

static string SelectBestBuildOutput(IReadOnlyList<string> dlls)
{
    var runtimeMajor = Environment.Version.Major;

    var candidates = dlls
        .Select(path => new
        {
            Path = path,
            TfmMajor = TryGetTfmMajor(path),
            LastWriteUtc = File.GetLastWriteTimeUtc(path),
        })
        .ToList();

    var exact = candidates
        .Where(c => c.TfmMajor == runtimeMajor)
        .OrderByDescending(c => c.LastWriteUtc)
        .FirstOrDefault();

    if (exact is not null)
        return exact.Path;

    var closestLower = candidates
        .Where(c => c.TfmMajor.HasValue && c.TfmMajor.Value < runtimeMajor)
        .OrderByDescending(c => c.TfmMajor)
        .ThenByDescending(c => c.LastWriteUtc)
        .FirstOrDefault();

    if (closestLower is not null)
        return closestLower.Path;

    var closestHigher = candidates
        .Where(c => c.TfmMajor.HasValue && c.TfmMajor.Value > runtimeMajor)
        .OrderBy(c => c.TfmMajor)
        .ThenByDescending(c => c.LastWriteUtc)
        .FirstOrDefault();

    if (closestHigher is not null)
        return closestHigher.Path;

    return candidates
        .OrderByDescending(c => c.LastWriteUtc)
        .First()
        .Path;
}

static int? TryGetTfmMajor(string assemblyPath)
{
    var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    foreach (var segment in assemblyPath.Split(separators, StringSplitOptions.RemoveEmptyEntries))
    {
        if (!segment.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            continue;

        var suffix = segment.AsSpan(3);
        var digitsLength = 0;

        while (digitsLength < suffix.Length && char.IsDigit(suffix[digitsLength]))
            digitsLength++;

        if (digitsLength == 0)
            continue;

        if (int.TryParse(suffix[..digitsLength], out var major))
            return major;
    }

    return null;
}

static bool HasReporterFlag(List<string> args)
{
    for (var i = 0; i < args.Count; i++)
    {
        if (args[i] == "--reporter")
            return true;
    }

    return false;
}

static void PrintToolHelp()
{
    Console.WriteLine("Usage: dotnet benchmark [--project <path>] [--assembly <path>] [host-options...]");
    Console.WriteLine();
    Console.WriteLine("Tool options:");
    Console.WriteLine("  --project <path>    Build and benchmark a .NET project (.csproj or directory)");
    Console.WriteLine("  --assembly <path>   Benchmark a specific assembly (.dll). Repeatable.");
    Console.WriteLine();
    Console.WriteLine("All BenchmarkHost flags pass through unchanged:");
    Console.WriteLine("  --filter, --iterations, --warmup, --reporter, --output, --confidence,");
    Console.WriteLine("  --alpha, --outlier, --auto-tune, --ops-per-sample, --ci-target,");
    Console.WriteLine("  --min-samples, --max-samples, --min-warmup, --max-warmup,");
    Console.WriteLine("  --max-tuning-time, --list, --dry-run, --in-process, --order, --seed,");
    Console.WriteLine("  --detail, --threshold-pct, --profile, --force-gc, --no-allocations, --help");
    Console.WriteLine();
    Console.WriteLine("See https://www.nbenchmark.net for the full CLI reference.");
}
