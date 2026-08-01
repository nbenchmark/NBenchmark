using System.Collections.Concurrent;

namespace NBenchmark.Workers;

/// <summary>
///     Keeps a few started-but-unassigned workers per runtime profile, so a launch is paid for while
///     something else is happening rather than on the critical path.
/// </summary>
/// <remarks>
///     <para>
///         This is a <b>pre-spawn</b> cache, not a recycling pool, and the distinction is the whole
///         design. A parked worker has completed its handshake but has not loaded a target assembly,
///         so handing it out is indistinguishable from having launched it just now. A worker that has
///         already measured something is never reused: unloading its target would require a
///         collectible load context, and a collectible context reaches static fields through a
///         <c>LoaderAllocator</c> indirection that inflates any benchmark touching a static - an
///         overhead the report would then attribute to the user's code.
///     </para>
///     <para>
///         Taking a worker triggers a background refill. Without that, only the first measurement in
///         a session would ever find one parked - which is the wrong shape for the case this exists
///         for, a test suite running many performance tests one after another.
///     </para>
///     <para>
///         Keyed by worker path <i>and</i> profile name. The profile is applied to the environment
///         block before the process starts, so a worker parked under one profile can never serve a
///         request for another. The path matters for the same reason and is easier to overlook: a
///         worker is framework-dependent and is chosen to match the assembly under test, so a
///         multi-runtime run or <c>dotnet benchmark --assembly</c> asks for a different
///         <c>nbworker</c> than the one this application sits beside. Keying on the profile alone
///         would hand a net10.0 worker out to measure a net8.0 build.
///     </para>
///     <para>
///         And by the synthesized runtimeconfig, for the same class of reason: a worker started
///         without <c>Microsoft.AspNetCore.App</c> cannot load a target that needs it (see
///         <see cref="SharedFrameworkConfig" />), and the framework set is fixed before the process
///         starts. A parked worker is only interchangeable with one launched now if all three inputs
///         to its launch matched.
///     </para>
/// </remarks>
internal static class WorkerPrewarm
{
    /// <summary>
    ///     How many workers to keep parked per profile.
    /// </summary>
    /// <remarks>
    ///     Small on purpose. Each parked worker is an idle process holding a runtime, and the point
    ///     is to cover the gap between finishing one measurement and starting the next - not to
    ///     build a fleet. One spare covers a sequential test suite; two covers a suite whose
    ///     framework overlaps teardown with the next test's setup.
    /// </remarks>
    internal static readonly int Depth = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);

    private static readonly ConcurrentDictionary<string, Pool> Pools = new(StringComparer.Ordinal);

    /// <summary>
    ///     Fills the pool for <paramref name="profile" /> up to <see cref="Depth" />.
    ///     Awaiting is optional - callers that want the latency hidden simply do not await.
    /// </summary>
    public static async Task PrimeAsync(
        RuntimeProfile? profile,
        string? workerAssemblyPath = null,
        string? runtimeConfigPath = null,
        CancellationToken cancellationToken = default)
    {
        if ((workerAssemblyPath ?? WorkerLocator.WorkerAssemblyPath) is not { } workerPath)
            return;

        var pool = Pools.GetOrAdd(KeyFor(workerPath, profile, runtimeConfigPath), _ => new Pool());

        while (pool.Reserve(Depth))
        {
            try
            {
                var worker = await WorkerHost
                    .StartAsync(workerPath, profile, runtimeConfigPath, cancellationToken)
                    .ConfigureAwait(false);

                pool.Park(worker);
            }
            catch
            {
                pool.Release();

                // A failure to pre-spawn is not a failure to measure: the direct-start path runs
                // next and surfaces the real error where the caller can act on it. Retrying here
                // would turn one broken deployment into a spawn loop.
                return;
            }
        }
    }

    /// <summary>
    ///     Takes a parked worker for this profile, or starts one. The returned worker belongs to the
    ///     caller, which owns disposing it.
    /// </summary>
    public static async Task<WorkerHost> TakeOrStartAsync(
        string workerAssemblyPath,
        RuntimeProfile? profile,
        string? runtimeConfigPath,
        CancellationToken cancellationToken)
    {
        var pool = Pools.GetOrAdd(KeyFor(workerAssemblyPath, profile, runtimeConfigPath), _ => new Pool());

        if (pool.TryTake(out var parked))
        {
            // Refilled in the background, so the *next* caller also finds one ready. Not awaited,
            // and not cancelled by this call's token - the refill outlives the request that
            // triggered it, and tying it to that token would cancel the refill the moment the
            // measurement it was meant to help finished. Refilled for the pool that was drained,
            // not for the application's own worker: those are the same file only in the modes where
            // the code under test is the running application.
            _ = Task.Run(
                () => PrimeAsync(profile, workerAssemblyPath, runtimeConfigPath, CancellationToken.None),
                CancellationToken.None);

            return parked;
        }

        return await WorkerHost
            .StartAsync(workerAssemblyPath, profile, runtimeConfigPath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Parked workers for one (worker path, runtime profile) pair.</summary>
    /// <remarks>
    ///     There is deliberately no drain method. Every worker is registered with
    ///     <see cref="Engine.ChildProcessReaper" /> at start, which kills the whole set on
    ///     <c>ProcessExit</c> and on Ctrl-C - so a parked worker cannot outlive this process whether
    ///     it was ever handed out or not. A second teardown path here would be one more thing to keep
    ///     in agreement with the first, for no coverage the first does not already give.
    /// </remarks>
    private sealed class Pool
    {
        private readonly ConcurrentQueue<WorkerHost> _ready = new();

        /// <summary>Parked plus in-flight, so concurrent primes do not collectively overshoot.</summary>
        private int _committed;

        /// <summary>Claims a slot, or reports that the pool is already at depth.</summary>
        public bool Reserve(int depth)
        {
            while (true)
            {
                var current = Volatile.Read(ref _committed);

                if (current >= depth)
                    return false;

                if (Interlocked.CompareExchange(ref _committed, current + 1, current) == current)
                    return true;
            }
        }

        public void Release() => Interlocked.Decrement(ref _committed);

        public void Park(WorkerHost worker) => _ready.Enqueue(worker);

        public bool TryTake(out WorkerHost worker)
        {
            if (!_ready.TryDequeue(out worker!))
                return false;

            Release();

            return true;
        }
    }

    /// <summary>
    ///     A profile that sets nothing is indistinguishable from no profile at all, so both park
    ///     under the same key rather than starting two identical processes.
    /// </summary>
    private static string KeyFor(string workerAssemblyPath, RuntimeProfile? profile, string? runtimeConfigPath)
    {
        var profileName = profile is null || profile.InheritsEverything
            ? RuntimeProfile.Host.Name
            : profile.Name;

        return $"{workerAssemblyPath}\0{profileName}\0{runtimeConfigPath}";
    }
}
