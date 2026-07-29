using System.Reflection;
using System.Runtime.InteropServices;

namespace NBenchmark.Workers;

/// <summary>
///     Finds <c>nbworker</c> on disk, and the <c>dotnet</c> muxer that runs it.
/// </summary>
/// <remarks>
///     The worker is a framework-dependent managed assembly launched with <c>dotnet exec</c>, not a
///     native executable. That is a deliberate packaging choice: a native launcher would have to
///     ship once per operating system and architecture for each of three target frameworks, and
///     since a .NET runtime is necessarily installed for the benchmark itself to run, the muxer is
///     always available.
/// </remarks>
internal static class WorkerLocator
{
    public const string WorkerAssemblyFileName = "nbworker.dll";

    /// <summary>
    ///     Explicit override, in the form of a full path to <c>nbworker.dll</c>. This is how a build
    ///     inside this repository points at the worker's own output directory, and how a user can
    ///     work around an unusual deployment layout without waiting for a fix.
    /// </summary>
    internal const string WorkerPathEnvVar = "NBENCHMARK_WORKER_PATH";

    /// <summary>
    ///     Assembly metadata key an application can carry to name the directory holding
    ///     <c>nbworker.dll</c>. Set by this repository's own test and sample projects at build time
    ///     so nothing has to guess at relative <c>bin</c> layouts.
    /// </summary>
    internal const string WorkerDirectoryMetadataKey = "NBenchmarkWorkerDirectory";

    private static readonly Lazy<string?> Cached = new(Locate);

    /// <summary>
    ///     The worker assembly path, or <c>null</c> when no worker is deployed. <c>null</c> is a
    ///     real state rather than an error: the caller falls back to in-process measurement and says
    ///     so, which is better than failing a run outright over a packaging problem.
    /// </summary>
    public static string? WorkerAssemblyPath => Cached.Value;

    /// <summary>
    ///     The worker deployed inside a specific build's output directory, or <c>null</c> when that
    ///     build has none.
    ///     <para>
    ///         Used for multi-runtime runs. A worker is framework-dependent, so the one that can
    ///         measure a net8.0 build is the net8.0 worker - and the build targets already copied it
    ///         next to that build's own assemblies. No search heuristics are needed: the right worker
    ///         is the one sitting beside the code under test.
    ///     </para>
    /// </summary>
    public static string? ForOutputDirectory(string? outputDirectory)
        => string.IsNullOrEmpty(outputDirectory)
            ? null
            : Path.Combine(outputDirectory, WorkerAssemblyFileName) is var candidate && File.Exists(candidate)
                ? Path.GetFullPath(candidate)
                : null;

    /// <summary>Explains where the worker was looked for, for a diagnostic the user can act on.</summary>
    public static string DescribeSearch()
    {
        var candidates = Candidates().ToList();

        return candidates.Count == 0
            ? "no candidate locations could be derived"
            : string.Join(", ", candidates.Select(c => $"'{c}'"));
    }

    private static string? Locate()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var overridePath = Environment.GetEnvironmentVariable(WorkerPathEnvVar);

        if (!string.IsNullOrWhiteSpace(overridePath))
            yield return overridePath.Trim();

        // The shipped layout: the package's build targets copy the worker into an `nbworker`
        // subdirectory of the application's output. Both the application base directory and the
        // engine assembly's own directory are checked, because they differ under a test host and
        // under any plugin-style deployment.
        foreach (var directory in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(typeof(WorkerLocator).Assembly.Location),
                 })
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            yield return Path.Combine(directory, "nbworker", WorkerAssemblyFileName);
            yield return Path.Combine(directory, WorkerAssemblyFileName);
        }

        foreach (var directory in MetadataDirectories())
        {
            yield return Path.Combine(directory, WorkerAssemblyFileName);
        }
    }

    /// <summary>
    ///     Directories named by <see cref="WorkerDirectoryMetadataKey" /> on the entry assembly, and
    ///     on the assembly that called in. Both are checked because under a test host the entry
    ///     assembly is the test runner, which carries no metadata of ours.
    /// </summary>
    private static IEnumerable<string> MetadataDirectories()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(WorkerLocator).Assembly })
        {
            if (assembly is null)
                continue;

            foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (metadata.Key == WorkerDirectoryMetadataKey && !string.IsNullOrWhiteSpace(metadata.Value))
                    yield return metadata.Value!;
            }
        }
    }


    /// <summary>
    ///     Resolves the <c>dotnet</c> muxer.
    ///     <para>
    ///         Derived from the running runtime's own directory first, because that is correct even
    ///         when the coordinator is a native apphost (where <c>Environment.ProcessPath</c> is the
    ///         application, not the muxer) and even when several .NET installations are present.
    ///         Falling back to the bare name lets <c>PATH</c> resolve it.
    ///     </para>
    /// </summary>
    public static string ResolveDotnetMuxer()
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // .../shared/Microsoft.NETCore.App/<version>/ -> .../
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();

        var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        var fromRuntime = Path.Combine(root, executable);

        if (File.Exists(fromRuntime))
            return fromRuntime;

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");

        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var fromEnv = Path.Combine(dotnetRoot.Trim(), executable);

            if (File.Exists(fromEnv))
                return fromEnv;
        }

        var processPath = Environment.ProcessPath;

        if (processPath is not null
            && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        return executable;
    }
}
