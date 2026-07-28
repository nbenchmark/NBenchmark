namespace NBenchmark.Workers;

/// <summary>
///     The seam between "decide what to measure" and "spawn a process and measure it".
///     <para>
///         Exists so the coordinator's planning logic - which benchmarks group together, how many
///         replicates, what happens when a worker dies - can be tested without spawning a process per
///         assertion. The real implementation is exercised end to end separately, by tests that do
///         spawn a worker; a seam alone is not coverage, which is exactly how the defect that emptied
///         raw samples on every isolated result managed to ship.
///     </para>
/// </summary>
internal interface IWorkerLauncher
{
    /// <summary>
    ///     Whether a worker can be launched at all. <c>false</c> when the worker is not deployed
    ///     beside the application, in which case the caller measures in-process and says so.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Measures one replicate of one group in a fresh worker.</summary>
    Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
        RunGroupPayload request,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal static class WorkerLauncher
{
    /// <summary>
    ///     The active launcher. Defaults to the real process-spawning implementation; tests
    ///     substitute a fake.
    /// </summary>
    internal static IWorkerLauncher Current { get; set; } = new ProcessWorkerLauncher();

    /// <summary>
    ///     Spawns a worker, measures one group in it, and shuts it down.
    ///     <para>
    ///         A worker is single-use. Reusing one across groups would need a collectible load
    ///         context to unload the previous target, and a collectible context reaches static fields
    ///         through a <c>LoaderAllocator</c> indirection that inflates any benchmark touching a
    ///         static - an overhead the report would then attribute to the user's code. Startup cost
    ///         is better hidden by pre-spawning than by recycling.
    ///     </para>
    /// </summary>
    private sealed class ProcessWorkerLauncher : IWorkerLauncher
    {
        public bool IsAvailable => WorkerLocator.WorkerAssemblyPath is not null;

        public async Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
            RunGroupPayload request,
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var workerPath = WorkerLocator.WorkerAssemblyPath;

            if (workerPath is null)
            {
                return new WorkerGroupRunner.GroupResult
                {
                    Results = [],
                    RawSamples = [],
                    Faults =
                    [
                        new FaultPayload
                        {
                            Message = "The measurement worker (nbworker) is not deployed alongside this "
                                      + $"application. Looked in {WorkerLocator.DescribeSearch()}.",
                        },
                    ],
                    WorkerDied = true,
                };
            }

            WorkerHost worker;

            try
            {
                worker = await WorkerPrewarm
                    .TakeOrStartAsync(workerPath, request.Options.RuntimeProfile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkerStartException ex)
            {
                return new WorkerGroupRunner.GroupResult
                {
                    Results = [],
                    RawSamples = [],
                    Faults = [new FaultPayload { Message = ex.Message }],
                    WorkerDied = true,
                };
            }

            await using (worker.ConfigureAwait(false))
            {
                return await WorkerGroupRunner
                    .RunAsync(worker, request, progress, observer, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
