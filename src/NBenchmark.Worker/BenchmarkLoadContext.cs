using System.Diagnostics;
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
///         Which is also why no instance of this is ever released, including the short-lived one
///         <c>WorkerSession.Construct</c> builds to resolve a strategy type name.
///         <see cref="AssemblyLoadContext" /> is not <see cref="IDisposable" />, and
///         <see cref="AssemblyLoadContext.Unload" /> throws on a non-collectible context - so there is
///         nothing to release, and making one collectible in order to release it would reintroduce the
///         measurement error above. The worker process is the unit of cleanup.
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
                //
                // DiagnosticSource is the exception, and the one case worth a diagnostic: falling
                // through gives the process two listener registries, which costs an isolated run
                // its entire telemetry stream and produces no other symptom. It can only happen if
                // the target asks for a version newer than the worker carries, so name the versions
                // - that is the whole fix.
                if (string.Equals(name, "System.Diagnostics.DiagnosticSource", StringComparison.Ordinal))
                {
                    Trace.TraceWarning(
                        "NBenchmark: the worker could not unify '{0}' (target wants {1}); telemetry from "
                        + "this process will not reach a listener attached in the default context. "
                        + "Raise the System.Diagnostics.DiagnosticSource version referenced by "
                        + "NBenchmark.Worker to {1} or higher.",
                        name, assemblyName.Version);
                }
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
    ///         where the target's own copy was meant to load. Naming the ones that matter costs one
    ///         line per package and cannot claim an assembly that is not ours.
    ///     </para>
    ///     <para>
    ///         <c>nbworker</c> and <c>nbenchmark-tool</c> are absent on purpose: they are entry-point
    ///         assemblies that a target never references, so there is no identity to unify.
    ///     </para>
    ///     <para>
    ///         <b><c>System.Diagnostics.DiagnosticSource</c> is here for a different reason than the
    ///         rest.</b> No type of its crosses the pipe. What must be unified is the process-wide
    ///         listener registry inside it: <see cref="System.Diagnostics.ActivitySource" /> and
    ///         <c>Meter</c> publish to static state, and an OpenTelemetry SDK loaded in this context
    ///         subscribes to whichever copy it binds against. NBenchmark's instruments live in the
    ///         default context, so a second copy here means the SDK listens to an empty registry and
    ///         an isolated run exports nothing at all - with no error anywhere, because both halves
    ///         are working exactly as written.
    ///     </para>
    ///     <para>
    ///         It is not hypothetical, and it is TFM-dependent, which is what makes it easy to ship
    ///         broken. <c>OpenTelemetry.Api</c> depends on <c>System.Diagnostics.DiagnosticSource</c>
    ///         10.0.0. Under <c>net10.0</c> the shared framework already supplies that, so nothing is
    ///         copied to the target's output and the question never arises. Under <c>net8.0</c> and
    ///         <c>net9.0</c> the framework supplies an older one, NuGet copies 10.0.0 next to the
    ///         target, and <see cref="AssemblyDependencyResolver" /> finds it. The worker therefore
    ///         carries its own <c>PackageReference</c> to the same package on those two frameworks,
    ///         so the default context has a version high enough to satisfy the bind - see
    ///         <c>NBenchmark.Worker.csproj</c>.
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
        "System.Diagnostics.DiagnosticSource",
    };

    private static bool IsEngineAssembly(string simpleName) => EngineAssemblies.Contains(simpleName);
}
