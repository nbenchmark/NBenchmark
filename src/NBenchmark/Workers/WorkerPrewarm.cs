using System.Collections.Concurrent;

namespace NBenchmark.Workers;

/// <summary>
///     Keeps at most one started-but-unassigned worker per runtime profile, so a launch can be paid
///     for while something else is happening rather than on the critical path.
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
///         Keyed by profile name because the runtime configuration is applied to the environment
///         block before the process starts; a worker parked under one profile can never serve a
///         request for another.
///     </para>
/// </remarks>
internal static class WorkerPrewarm
{
    private static readonly ConcurrentDictionary<string, WorkerHost> Parked = new(StringComparer.Ordinal);

    /// <summary>
    ///     Starts a worker for <paramref name="profile" /> and parks it, unless one is already parked.
    ///     Awaiting this is optional - callers that want the latency hidden simply do not await.
    /// </summary>
    public static async Task PrimeAsync(RuntimeProfile? profile, CancellationToken cancellationToken = default)
    {
        var workerPath = WorkerLocator.WorkerAssemblyPath;

        if (workerPath is null)
            return;

        var key = KeyFor(profile);

        if (Parked.ContainsKey(key))
            return;

        var worker = await WorkerHost.StartAsync(workerPath, profile, cancellationToken).ConfigureAwait(false);

        if (!Parked.TryAdd(key, worker))
        {
            // Another caller parked one first. Two idle workers is waste, not a bug, so the loser
            // shuts its own down rather than leaving it to the reaper at exit.
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Takes the parked worker for this profile, or starts one. The returned worker belongs to
    ///     the caller, which owns disposing it.
    /// </summary>
    public static async Task<WorkerHost> TakeOrStartAsync(
        string workerAssemblyPath,
        RuntimeProfile? profile,
        CancellationToken cancellationToken)
    {
        if (Parked.TryRemove(KeyFor(profile), out var parked))
            return parked;

        return await WorkerHost.StartAsync(workerAssemblyPath, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Shuts down anything still parked. A parked worker is idle and harmless, but leaving one
    ///     alive past the end of a run would surprise anyone watching their process list.
    /// </summary>
    public static async Task DrainAsync()
    {
        foreach (var key in Parked.Keys.ToArray())
        {
            if (Parked.TryRemove(key, out var worker))
                await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     A profile that sets nothing is indistinguishable from no profile at all, so both park
    ///     under the same key rather than starting two identical processes.
    /// </summary>
    private static string KeyFor(RuntimeProfile? profile)
        => profile is null || profile.InheritsEverything ? RuntimeProfile.Host.Name : profile.Name;
}
