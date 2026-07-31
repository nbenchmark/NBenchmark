using System.Reflection;
using System.Runtime.Loader;

namespace NBenchmark.Worker;

/// <summary>
///     Loads the assembly under test, and its dependency graph, into the worker.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not collectible.</b> A collectible context is the obvious way to recycle a warm
///         worker across groups, and it is disqualified: a collectible context reaches its static
///         fields through a <c>LoaderAllocator</c> indirection, which inflates any benchmark that
///         touches a static by a factor the measurement then reports as the user's code. A worker
///         is therefore single-purpose and exits when its group is done. Startup cost is hidden by
///         pre-spawning instead of by recycling.
///     </para>
///     <para>
///         <b>NBenchmark itself is unified, not reloaded.</b> The target assembly references
///         NBenchmark - that is where <c>[Benchmark]</c> comes from - and its output directory
///         contains a copy of <c>NBenchmark.dll</c>. Loading that copy here would produce a second,
///         distinct <c>BenchmarkAttribute</c> type, and the worker's discovery pass would find no
///         benchmarks at all while reporting no error. Deferring those names to the default context
///         keeps one type identity across the boundary.
///     </para>
/// </remarks>
internal sealed class BenchmarkLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public BenchmarkLoadContext(string targetAssemblyPath)
        : base(name: $"nbworker:{Path.GetFileNameWithoutExtension(targetAssemblyPath)}", isCollectible: false)
    {
        // Rooted at the *defining* assembly rather than the entry assembly. A benchmark body can
        // live anywhere in the graph, and under a test host the entry assembly is the test
        // runner, whose .deps.json says nothing about the code under test.
        _resolver = new AssemblyDependencyResolver(targetAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;

        if (name is not null && IsEngineAssembly(name))
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
                // The worker does not ship every NBenchmark satellite - reporter and integration
                // packages are the user's choice. Anything the worker does not have falls through
                // to the target's own copy, which is correct: those assemblies do not define types
                // that cross the process boundary, so a separate identity for them is harmless.
            }
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>
    ///     The assemblies that carry types appearing on both sides of the process boundary, and must
    ///     therefore have a single identity.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An explicit list rather than an <c>NBenchmark.</c> prefix match. Nothing in this repo is
    ///         strong-named, so a simple name is the only thing available to match on - which means a
    ///         prefix test also captures any third-party or user assembly that happens to be called
    ///         <c>NBenchmark.Something</c>, and silently redirects it to the worker's default context
    ///         where the target's own copy was meant to load. Naming the seven that matter costs one
    ///         line per package and cannot claim an assembly that is not ours.
    ///     </para>
    ///     <para>
    ///         <c>nbworker</c> and <c>nbenchmark-tool</c> are absent on purpose: they are entry-point
    ///         assemblies that a target never references, so there is no identity to unify.
    ///     </para>
    /// </remarks>
    private static readonly HashSet<string> EngineAssemblies = new(StringComparer.Ordinal)
    {
        "NBenchmark",
        "NBenchmark.Integration.Abstractions",
        "NBenchmark.Integration.xUnit",
        "NBenchmark.Integration.NUnit",
        "NBenchmark.Integration.MSTest",
        "NBenchmark.DependencyInjection",
        "NBenchmark.Reporters.Console",
    };

    private static bool IsEngineAssembly(string simpleName) => EngineAssemblies.Contains(simpleName);
}
