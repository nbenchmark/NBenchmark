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
///         Keyed by profile name because the runtime configuration is applied to the environment
///         block before the process starts; a worker parked under one profile can never serve a
///         request for another.
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
    public static async Task PrimeAsync(RuntimeProfile? profile, CancellationToken cancellationToken = default)
    {
        if (WorkerLocator.WorkerAssemblyPath is not { } workerPath)
            return;

        var pool = Pools.GetOrAdd(KeyFor(profile), _ => new Pool());

        while (pool.Reserve(Depth))
        {
            try
            {
                var worker = await WorkerHost.StartAsync(workerPath, profile, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var pool = Pools.GetOrAdd(KeyFor(profile), _ => new Pool());

        if (pool.TryTake(out var parked))
        {
            // Refilled in the background, so the *next* caller also finds one ready. Not awaited,
            // and not cancelled by this call's token - the refill outlives the request that
            // triggered it, and tying it to that token would cancel the refill the moment the
            // measurement it was meant to help finished.
            _ = Task.Run(() => PrimeAsync(profile, CancellationToken.None), CancellationToken.None);

            return parked;
        }

        return await WorkerHost.StartAsync(workerAssemblyPath, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Shuts down everything still parked. A parked worker is idle and harmless, but leaving one
    ///     alive past the end of a run would surprise anyone watching their process list.
    /// </summary>
    public static async Task DrainAsync()
    {
        foreach (var key in Pools.Keys.ToArray())
        {
            if (!Pools.TryRemove(key, out var pool))
                continue;

            while (pool.TryTake(out var worker))
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Parked workers for one runtime profile.</summary>
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
    private static string KeyFor(RuntimeProfile? profile)
        => profile is null || profile.InheritsEverything ? RuntimeProfile.Host.Name : profile.Name;
}
