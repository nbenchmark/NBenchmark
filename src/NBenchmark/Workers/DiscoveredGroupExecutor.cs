using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;

namespace NBenchmark.Workers;

/// <summary>
///     Runs a group of attribute-discovered benchmarks from one class: instance creation,
///     setup/teardown, the shared-instance reset hook, and the per-lifetime execution shape.
///     <para>
///         This is the single implementation shared by the measurement worker and the host
///         process. Having one copy is not tidiness - it is the correctness property. The worker
///         exists so that a measurement can be taken under a runtime configuration the host
///         cannot have, and nothing else. If the worker ran a second, parallel implementation of
///         instance lifetime and setup ordering, an isolated number and an in-process number
///         would differ for reasons unrelated to the process boundary, and the comparison the
///         whole design rests on would be meaningless.
///     </para>
/// </summary>
internal static class DiscoveredGroupExecutor
{
    /// <summary>
    ///     The outcome of running a group. <see cref="InstantiationFailed" /> is reported rather
    ///     than signalled through <c>Environment.ExitCode</c>, because a worker serves many groups
    ///     in its lifetime and must be able to report one group's failure without ending itself.
    /// </summary>
    internal readonly record struct GroupOutcome(
        List<BenchmarkResult> Results,
        Dictionary<string, double[]> RawSamples,
        bool InstantiationFailed)
    {
        /// <summary>
        ///     Why construction failed, for the fault the worker reports. Carried rather than left on
        ///     the worker's stdout: the coordinator turns this into the errored rows the user reads,
        ///     and "could not be instantiated" without the reason sends them nowhere.
        /// </summary>
        public string? Failure { get; private init; }

        public static GroupOutcome Failed(string? failure = null)
            => new([], [], true) { Failure = failure };
    }

    public static async Task<GroupOutcome> RunAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> selected,
        MeasurementOptions options,
        Func<Type, InstanceHandle>? instanceFactory,
        RunOrder order,
        int? seed,
        int startIndex,
        int totalBenchmarks,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        Action? postSuiteCleanup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(selected);

        return suite.Lifetime == InstanceLifetime.PerClass
            ? await RunPerClassAsync(
                    suite, selected, options, instanceFactory, order, seed, startIndex, totalBenchmarks,
                    progress, observer, postSuiteCleanup, cancellationToken)
                .ConfigureAwait(false)
            : await RunPerMethodAsync(
                    suite, selected, options, instanceFactory, order, seed, startIndex, totalBenchmarks,
                    progress, observer, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    ///     One instance shared by every benchmark in the group, with setup run once before the
    ///     first and <see cref="IStateReset.ResetAsync" /> fired in each gap.
    /// </summary>
    private static async Task<GroupOutcome> RunPerClassAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> selected,
        MeasurementOptions options,
        Func<Type, InstanceHandle>? instanceFactory,
        RunOrder order,
        int? seed,
        int startIndex,
        int totalBenchmarks,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        Action? postSuiteCleanup,
        CancellationToken cancellationToken)
    {
        var created = BenchmarkLifecycle.CreateInstance(suite.Type, instanceFactory, out var failure);

        if (created is null)
            return GroupOutcome.Failed(failure);

        var (instance, instanceTeardown) = created.Value;
        var instanceFromFactory = instanceFactory is not null;

        List<BenchmarkResult> results;
        var samples = new Dictionary<string, double[]>();

        try
        {
            var selectedSuite = suite with { Benchmarks = selected };
            var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(selectedSuite, instance, options);

            // The shared-instance independence warning is a property of the suite, not of any one
            // result, so it is known before the first benchmark starts. Computing it here - before
            // the run, rather than after - is what gets it onto the wire. W-44 sends each result as it
            // completes (via StreamingProgress.OnBenchmarkCompletedAsync), so a warning attached to the
            // local results list after the run would land on objects that had already crossed the
            // process boundary without it. The decorator below stamps it on each result ahead of the
            // inner sink, which is the moment a worker puts it on the wire. The post-run Attach
            // (kept) does the same for the returned list, for any caller that reads the outcome's
            // results directly; the wire copy and the list copy are independent objects, so the
            // warning appears once on each.
            //
            // Computed before the setup check so both branches - setup failed, or the run produced
            // results - send through the same decorated sink, and a setup failure carries the warning
            // too.
            var dependenceWarning = InstanceIndependence.DependenceWarning(
                suite.Type, suite.Lifetime, selected.Count, options);

            var reported = dependenceWarning is null
                ? progress
                : new IndependenceWarningProgress(progress, dependenceWarning);

            if (!setupSuccess)
            {
                results = setupErrors!.ToList();

                // Setup threw before any benchmark ran, so SuiteRunner never raised
                // OnBenchmarkCompletedAsync for these errored rows. Send them through the same sink so
                // they reach the coordinator the way measured results do - without this, the only
                // copy lived in the local list the worker no longer batches and ships at group end.
                foreach (var error in results)
                    await reported.OnBenchmarkCompletedAsync(error, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var factory = () => instance;
                var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

                var envelopes = selected
                    .Select(b => BenchmarkEnvelope.FromDiscovered(b, qualifiedClassName, factory))
                    .ToList();

                Func<Task>? betweenBenchmarksReset = InstanceIndependence.ResetsItself(suite.Type)
                    ? () => ((IStateReset)instance).ResetAsync(cancellationToken)
                    : null;

                (results, samples) = await SuiteRunner.RunAsync(
                        envelopes, order, seed, options,
                        startIndex, totalBenchmarks, reported, cancellationToken,
                        betweenBenchmarksReset, observer)
                    .ConfigureAwait(false);

                // Raised here rather than in the coordinator's in-process path, which is where it
                // used to live and is the path a default Harness run never takes. Sharing an
                // instance breaks the independence assumption identically in a worker; the worker
                // was simply the one measuring process that never said so.
                InstanceIndependence.Attach(results, dependenceWarning);
            }
        }
        finally
        {
            await BenchmarkLifecycle
                .RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, postSuiteCleanup)
                .ConfigureAwait(false);
        }

        return new GroupOutcome(results, samples, false);
    }

    /// <summary>
    ///     A fresh instance per benchmark, each with its own setup and teardown. Run order is
    ///     applied here rather than inside <see cref="SuiteRunner" />, because each benchmark gets
    ///     its own single-envelope invocation and <see cref="SuiteRunner" /> would have nothing to
    ///     reorder.
    /// </summary>
    private static async Task<GroupOutcome> RunPerMethodAsync(
        BenchmarkSuiteDefinition suite,
        IReadOnlyList<BenchmarkMethodDefinition> selected,
        MeasurementOptions options,
        Func<Type, InstanceHandle>? instanceFactory,
        RunOrder order,
        int? seed,
        int startIndex,
        int totalBenchmarks,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var results = new List<BenchmarkResult>();
        var samples = new Dictionary<string, double[]>();
        var ordered = RunOrdering.Apply(selected, order, seed);
        var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

        for (var index = 0; index < ordered.Count; index++)
        {
            var benchmark = ordered[index];
            var created = BenchmarkLifecycle.CreateInstance(suite.Type, instanceFactory, out var failure);

            if (created is null)
            {
                // One benchmark's dependency problem costs that benchmark, not the group. This used to
                // return GroupOutcome.Failed: benchmark 3 of 5 needing a service that was registered
                // only conditionally abandoned 4 and 5, discarded the results already accumulated, and
                // surfaced as a whole-class fault with no benchmark name on it. A *setup* failure at
                // the same point in the same loop has always produced errored rows and continued, and
                // there was never a reason for two adjacent user-code failures to differ.
                var uninstantiable = OutcomeBuilder.Build(
                    new RunOutcome.Errored(new BenchmarkExecutionException(failure!), failure!),
                    BenchmarkEnvelope.QualifiedDiscoveredBenchmarkName(suite.Type, benchmark.DisplayName),
                    qualifiedClassName,
                    benchmark.Attribute.Description,
                    benchmark.IsBaseline,
                    options,
                    TimeSpan.Zero,
                    TimeSpan.Zero).Result;

                results.Add(uninstantiable);

                await progress.OnBenchmarkCompletedAsync(uninstantiable, cancellationToken).ConfigureAwait(false);

                continue;
            }

            var (instance, instanceTeardown) = created.Value;
            var instanceFromFactory = instanceFactory is not null;

            try
            {
                var singleBenchmarkSuite = suite with { Benchmarks = [benchmark] };
                var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(singleBenchmarkSuite, instance, options);

                if (!setupSuccess)
                {
                    // Setup threw, so SuiteRunner never ran and never raised OnBenchmarkCompletedAsync for
                    // these errored rows. Send them through the same sink so they reach the
                    // coordinator the way measured results do - without this, the only copy lived in
                    // the local list the worker no longer batches and ships at group end.
                    foreach (var error in setupErrors!)
                    {
                        results.Add(error);
                        await progress.OnBenchmarkCompletedAsync(error, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var factory = () => instance;
                var envelope = BenchmarkEnvelope.FromDiscovered(benchmark, qualifiedClassName, factory);

                var (batchResults, batchSamples) = await SuiteRunner.RunAsync(
                        [envelope], RunOrder.Declaration, null, options,
                        startIndex + index, totalBenchmarks, progress, cancellationToken,
                        null, observer)
                    .ConfigureAwait(false);

                results.AddRange(batchResults);

                foreach (var (name, values) in batchSamples)
                {
                    samples[name] = values;
                }
            }
            finally
            {
                await BenchmarkLifecycle
                    .RunTeardown(suite, instance, instanceFromFactory, instanceTeardown, null)
                    .ConfigureAwait(false);
            }
        }

        return new GroupOutcome(results, samples, false);
    }

    /// <summary>
    ///     Wraps an <see cref="IBenchmarkProgress" /> so each completed result carries the
    ///     shared-instance independence warning before the inner sink reports it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A worker sends each result the moment it completes (W-44), not in a batch at group end,
    ///         so a warning attached to the local results list <i>after</i> the run would never reach
    ///         the coordinator - the result it should have been stamped on is already gone. This
    ///         decorator stamps it on the result inside <see cref="OnBenchmarkCompletedAsync" />, ahead of
    ///         the inner sink, which is the exact moment a worker puts the result on the wire.
    ///     </para>
    ///     <para>
    ///         Only the PerClass path carries this warning, so only that path wraps; PerMethod has
    ///         nothing to warn about and runs unwrapped. The observer facet
    ///         (<see cref="IMeasurementObserver" />) is the inner sink's own and is passed separately,
    ///         so wrapping the progress facet does not touch it.
    ///     </para>
    /// </remarks>
    private sealed class IndependenceWarningProgress(IBenchmarkProgress inner, string warning)
        : IBenchmarkProgress
    {
        // Every member forwards. A decorator that left one to the interface's no-op default would
        // silently swallow that event instead of passing it to the inner sink.
        public Task OnSuiteStartingAsync(
            IReadOnlyList<string> benchmarkNames, int total, CancellationToken cancellationToken)
            => inner.OnSuiteStartingAsync(benchmarkNames, total, cancellationToken);

        public Task OnWarmupStartingAsync(string name, int totalWarmupSamples, CancellationToken cancellationToken)
            => inner.OnWarmupStartingAsync(name, totalWarmupSamples, cancellationToken);

        public Task OnWarmupCompletedAsync(string name, CancellationToken cancellationToken)
            => inner.OnWarmupCompletedAsync(name, cancellationToken);

        public Task OnBenchmarkStartingAsync(string name, int index, int total, CancellationToken cancellationToken)
            => inner.OnBenchmarkStartingAsync(name, index, total, cancellationToken);

        public Task OnSampleCompletedAsync(
            string name, int sample, int totalSamples, CancellationToken cancellationToken)
            => inner.OnSampleCompletedAsync(name, sample, totalSamples, cancellationToken);

        public Task OnBenchmarkCompletedAsync(BenchmarkResult result, CancellationToken cancellationToken)
        {
            // The same attachment InstanceIndependence.Attach applies to a list, applied to one
            // result: append the warning, preserving anything already there.
            var warned = result with
            {
                Warnings = result.Warnings.Count > 0
                    ? [.. result.Warnings, warning]
                    : [warning],
            };

            return inner.OnBenchmarkCompletedAsync(warned, cancellationToken);
        }

        public Task OnSuiteCompletedAsync(
            IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
            => inner.OnSuiteCompletedAsync(results, cancellationToken);
    }
}
