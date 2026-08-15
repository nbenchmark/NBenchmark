using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NBenchmark.Diagnostics;
using NBenchmark.Engine;
using NBenchmark.Observers;
using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     Loads the <c>NBenchmark.*</c> satellite packages the target assembly references, so their
///     <c>[ModuleInitializer]</c> self-registration runs in the worker as it does in the harness.
/// </summary>
/// <remarks>
///     <para>
///         The coordinator gets this for free. <c>ObserverRegistry.EnsureExtensionsLoaded</c> and its
///         reporter-side twin walk <see cref="Assembly.GetEntryAssembly" />'s references and load
///         anything named <c>NBenchmark.*</c>, which is what makes <c>--reporter markdown</c> and an
///         auto-attached observer work without the user naming an assembly.
///     </para>
///     <para>
///         In a worker the entry assembly is <c>nbworker</c>, which references NBenchmark and nothing
///         of the user's, so that walk finds none of their packages. Rooting the same walk at the
///         *target* assembly finds them: the target is the project that took the dependency, which is
///         precisely the statement "this run wants that extension".
///     </para>
///     <para>
///         Loading is the entire mechanism. A satellite registers itself from a module initializer,
///         and the runtime runs one before the first access to anything in its module - so the
///         <see cref="Assembly.Load(AssemblyName)" /> call below is what puts an exporter or an
///         observer into the registry. Nothing here decides whether it then does anything; that is
///         the factory's business, and a factory with nothing to do returns the null observer.
///     </para>
/// </remarks>
internal static class ExtensionLoader
{
    private static readonly HashSet<string> Loaded = new(StringComparer.Ordinal);

    private static IMeasurementObserver? _autoAttached;

    /// <summary>
    ///     Loads the target's extensions and returns the auto-attached observers they registered,
    ///     resolved once per worker process. Repeat calls return the same instance, so a worker
    ///     measuring several groups activates its exporter once rather than once per group.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Resolution has to happen here rather than at session start, because the registry is
    ///         empty until the target's packages are loaded and the target is not known until the
    ///         first group arrives.
    ///     </para>
    ///     <para>
    ///         The returned observer is not wired into the measurement. An exporter needs no
    ///         callbacks - the engine's <c>Meter</c> and <c>ActivitySource</c> emit on their own, and
    ///         the SDK the observer constructed is listening to them - so what is wanted from it is
    ///         construction and disposal, not <c>OnSample</c>. Passing it to the loop instead would
    ///         put a fan-out on the hot path to deliver events nothing reads.
    ///     </para>
    /// </remarks>
    public static IMeasurementObserver ActivateExtensions(BenchmarkLoadContext context, Assembly target)
    {
        if (_autoAttached is not null)
            return _autoAttached;

        LoadReferencedExtensions(context, target);

        _autoAttached = ObserverRegistry.CreateAutoAttachedObservers(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)) switch
        {
            { Count: 0 } => NullMeasurementObserver.Instance,
            { Count: 1 } single => single[0],
            var many => new CompositeMeasurementObserver(many),
        };

        // Strictly after the observers are constructed, because constructing an exporter is what
        // attaches a listener to the NBenchmark ActivitySource - and StartActivity returns null
        // when nothing is listening. Opened here rather than at session start for that reason: at
        // session start there is no target assembly yet, so there is no exporter yet either.
        NBenchmarkDiagnostics.OnWorkerSessionStarting(
            Environment.GetEnvironmentVariable(MeasurementBudget.TraceParentEnvVar));

        return _autoAttached;
    }

    /// <summary>
    ///     Disposes whatever <see cref="ActivateExtensions" /> resolved. This is the flush point for
    ///     an exporter: deterministic, unlike a <c>ProcessExit</c> handler, and reached on every exit
    ///     path the session has.
    /// </summary>
    public static void Deactivate()
    {
        // Before the flush below, or the worker's own span is still open when its exporter ships
        // the batch and the trace arrives missing the node every other span hangs from.
        NBenchmarkDiagnostics.OnWorkerSessionCompleted();

        var observer = Interlocked.Exchange(ref _autoAttached, null);

        if (observer is null || observer == NullMeasurementObserver.Instance)
            return;

        try
        {
            observer.Dispose();
        }
        catch (Exception ex)
        {
            // A failed flush must not change the worker's exit code: the measurements are already
            // streamed and the coordinator reads an exit code to decide whether they can be trusted.
            Trace.TraceWarning("NBenchmark: the worker failed to dispose an auto-attached observer: {0}", ex.Message);
        }
    }

    /// <summary>
    ///     Loads every <c>NBenchmark.*</c> assembly <paramref name="target" /> references, once per
    ///     worker process. Failures are traced and swallowed: an extension that cannot load is a
    ///     missing feature, not a reason to lose the measurement the worker was started for.
    /// </summary>
    private static void LoadReferencedExtensions(BenchmarkLoadContext context, Assembly target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);

        foreach (var reference in target.GetReferencedAssemblies())
        {
            var name = reference.Name;

            // NBenchmark itself is already loaded - it is how we got here - and is unified to the
            // default context by BenchmarkLoadContext in any case.
            if (name is null
                || !name.StartsWith("NBenchmark.", StringComparison.Ordinal)
                || !Loaded.Add(name))
                continue;

            try
            {
                // Through the target's load context, not Assembly.Load. Assembly.Load resolves in
                // the calling assembly's context - here nbworker's, whose directory holds the
                // engine and nothing of the user's - so it fails for precisely the packages this
                // exists to load. The context resolves against the target's own deps.json, which is
                // where a satellite the target references actually lives.
                var extension = context.LoadFromAssemblyName(reference);

                // Loading is not enough, which is the whole subtlety of this file. A module
                // initializer runs before the first *access* to something in the module, and
                // loading an assembly nobody then touches is not an access - so the package sat
                // loaded and unregistered, and the registry came back empty. Nothing in the worker
                // ever touches these types by name (that is the point of a registry), so the
                // initializer has to be run explicitly.
                RuntimeHelpers.RunModuleConstructor(extension.ManifestModule.ModuleHandle);
            }
            catch (Exception ex)
            {
                // Mirrors ObserverRegistry.EnsureExtensionsLoaded: traced rather than printed, so a
                // host with a listener attached can see why an extension never appeared without the
                // worker writing to a stream the coordinator parses.
                Trace.TraceWarning(
                    "NBenchmark: the worker failed to load extension assembly '{0}': {1}",
                    name, ex.Message);
            }
        }
    }
}
