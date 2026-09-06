using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    ///     Whether a worker can be launched to measure benchmarks declared in
    ///     <paramref name="targetAssemblyPath" />.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="IsAvailable" /> because the two differ whenever the code under
    ///     test is not the running application - under <c>dotnet benchmark --assembly</c> the target
    ///     is a separate build with its own worker beside it, and the tool's own directory has none.
    ///     Asking the application-wide question there answers <c>false</c> and drops to in-process
    ///     measurement while a perfectly good worker sits next to the target.
    /// </remarks>
    bool IsAvailableFor(string? targetAssemblyPath) => IsAvailable;

    /// <summary>Measures one replicate of one group in a fresh worker.</summary>
    Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
        RunGroupPayload request,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

[RequiresUnreferencedCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
[RequiresDynamicCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
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
    [RequiresUnreferencedCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
    [RequiresDynamicCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
    private sealed class ProcessWorkerLauncher : IWorkerLauncher
    {
        public bool IsAvailable => WorkerLocator.WorkerAssemblyPath is not null;

        public bool IsAvailableFor(string? targetAssemblyPath)
            => WorkerLocator.ForAssembly(targetAssemblyPath) is not null || IsAvailable;

        public async Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
            RunGroupPayload request,
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Most specific first. An explicit path wins - a multi-runtime run measures a build for
            // another target framework and names that framework's worker. Otherwise the worker beside
            // the code under test, which is the one built against it and therefore the one that can
            // load it. The application's own worker is the fallback, and in every mode where the
            // application *is* the code under test it is the same file.
            var workerPath = request.WorkerAssemblyPath
                             ?? WorkerLocator.ForAssembly(request.TargetAssemblyPath)
                             ?? WorkerLocator.WorkerAssemblyPath;

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
                                      + "application, nor beside the assembly under test. Looked in "
                                      + $"{WorkerLocator.DescribeSearch(request.TargetAssemblyPath)}.",
                        },
                    ],
                    WorkerDied = true,
                };
            }

            // A target built against a shared framework the worker does not declare - an ASP.NET
            // Core project is the ordinary case - cannot be loaded by a worker started without it,
            // and the framework set is fixed before the process starts. Null for every other target,
            // which leaves the launch unchanged.
            var runtimeConfigPath = SharedFrameworkConfig.ResolveFor(workerPath, request.TargetAssemblyPath);

            WorkerHost worker;

            try
            {
                worker = await WorkerPrewarm
                    .TakeOrStartAsync(
                        workerPath,
                        request.Options.RuntimeProfile,
                        runtimeConfigPath,
                        cancellationToken)
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
