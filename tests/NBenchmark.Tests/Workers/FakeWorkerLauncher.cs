using NBenchmark.Workers;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Substitutes for the real process-spawning launcher so tests can assert what the coordinator
///     <i>asked</i> for - grouping, replicate count, run order, options - without paying a process
///     launch per assertion.
///     <para>
///         This is a planning seam, not a substitute for end-to-end coverage. The defect that emptied
///         raw samples on every isolated result survived precisely because a fake was the only thing
///         any isolation test ever exercised, so the real path is covered separately by
///         <see cref="RealWorkerTests" />, which spawns actual workers.
///     </para>
/// </summary>
internal sealed class FakeWorkerLauncher(
    Func<RunGroupPayload, WorkerGroupRunner.GroupResult> handler) : IWorkerLauncher
{
    private readonly Func<RunGroupPayload, WorkerGroupRunner.GroupResult> _handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>Every request the coordinator issued, in order.</summary>
    public List<RunGroupPayload> Requests { get; } = [];

    public bool IsAvailable => true;

    public Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
        RunGroupPayload request,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(_handler(request));
    }

    /// <summary>
    ///     Installs this launcher for the duration of the returned scope. Restores the previous one
    ///     on dispose, so a test never leaks a fake into the rest of the suite.
    /// </summary>
    public static Scope Install(Func<RunGroupPayload, WorkerGroupRunner.GroupResult> handler)
    {
        var fake = new FakeWorkerLauncher(handler);
        var prior = WorkerLauncher.Current;
        WorkerLauncher.Current = fake;

        return new Scope(fake, prior);
    }

    /// <summary>
    ///     A launcher that reports no worker is deployed, for testing the honest in-process fallback.
    /// </summary>
    public static Scope InstallUnavailable()
    {
        var prior = WorkerLauncher.Current;
        WorkerLauncher.Current = new UnavailableLauncher();

        return new Scope(null, prior);
    }

    internal sealed class Scope(FakeWorkerLauncher? launcher, IWorkerLauncher prior) : IDisposable
    {
        public FakeWorkerLauncher Launcher =>
            launcher ?? throw new InvalidOperationException("This scope installed an unavailable launcher.");

        public void Dispose() => WorkerLauncher.Current = prior;
    }

    private sealed class UnavailableLauncher : IWorkerLauncher
    {
        public bool IsAvailable => false;

        public Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
            RunGroupPayload request,
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "An unavailable launcher must never be asked to run a group; the coordinator should "
                + "have fallen back to in-process measurement.");
    }
}
