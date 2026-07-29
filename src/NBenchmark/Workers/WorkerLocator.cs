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
    {
        if (string.IsNullOrEmpty(outputDirectory))
            return null;

        foreach (var candidate in DirectoryCandidates(outputDirectory))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    ///     The worker deployed beside <paramref name="assemblyPath" />, or <c>null</c> when there is
    ///     none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The assembly that <i>declares</i> the benchmarks is not always the one that is running.
    ///         Under <c>dotnet benchmark --assembly</c> the two are entirely different builds, in
    ///         different directories, potentially targeting different frameworks. A worker is
    ///         framework-dependent, so the one that can measure a given build is the one that build
    ///         deployed - searching beside the running application instead finds a worker for the
    ///         wrong framework, or none at all, and silently falls back to in-process measurement.
    ///     </para>
    ///     <para>
    ///         For every other usage mode the declaring assembly <i>is</i> the application, so this
    ///         resolves to the same file the general search would have found.
    ///     </para>
    /// </remarks>
    public static string? ForAssembly(string? assemblyPath)
        => string.IsNullOrEmpty(assemblyPath)
            ? null
            : ForOutputDirectory(Path.GetDirectoryName(assemblyPath));

    /// <summary>
    ///     Both deployment layouts, in the order the general search uses them: the <c>nbworker</c>
    ///     subdirectory the build targets produce, then the directory itself.
    /// </summary>
    /// <remarks>
    ///     The subdirectory is the shipped layout - the worker carries its own
    ///     <c>runtimeconfig.json</c> and <c>deps.json</c>, which describe a different program and must
    ///     not sit beside the application's. <see cref="ForOutputDirectory" /> checked only the flat
    ///     form, which happens to be present in an in-repo build and is not what a package consumer
    ///     gets.
    /// </remarks>
    private static IEnumerable<string> DirectoryCandidates(string directory)
    {
        yield return Path.Combine(directory, "nbworker", WorkerAssemblyFileName);
        yield return Path.Combine(directory, WorkerAssemblyFileName);
    }

    /// <summary>Explains where the worker was looked for, for a diagnostic the user can act on.</summary>
    /// <param name="targetAssemblyPath">
    ///     The assembly declaring the benchmarks, when it is known and differs from the running
    ///     application. Named first because it is where the worker <i>should</i> be, and because a
    ///     diagnostic listing only the application's own directory sends the reader to fix the wrong
    ///     build.
    /// </param>
    public static string DescribeSearch(string? targetAssemblyPath = null)
    {
        var candidates = new List<string>();

        if (Path.GetDirectoryName(targetAssemblyPath) is { Length: > 0 } targetDirectory)
            candidates.AddRange(DirectoryCandidates(targetDirectory));

        candidates.AddRange(Candidates());

        return candidates.Count == 0
            ? "no candidate locations could be derived"
            : string.Join(", ", candidates.Distinct(StringComparer.Ordinal).Select(c => $"'{c}'"));
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

            foreach (var candidate in DirectoryCandidates(directory))
            {
                yield return candidate;
            }
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
