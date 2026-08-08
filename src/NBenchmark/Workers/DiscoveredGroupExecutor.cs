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

            if (!setupSuccess)
                results = setupErrors!.ToList();
            else
            {
                var factory = () => instance;

                var envelopes = selected
                    .Select(b => BenchmarkEnvelope.FromDiscovered(b, suite.Type.Name, factory))
                    .ToList();

                Func<Task>? betweenBenchmarksReset = InstanceIndependence.ResetsItself(suite.Type)
                    ? () => ((IStateReset)instance).ResetAsync(cancellationToken)
                    : null;

                (results, samples) = await SuiteRunner.RunAsync(
                        envelopes, order, seed, options,
                        startIndex, totalBenchmarks, progress, cancellationToken,
                        betweenBenchmarksReset, observer)
                    .ConfigureAwait(false);

                // Raised here rather than in the coordinator's in-process path, which is where it
                // used to live and is the path a default Harness run never takes. Sharing an
                // instance breaks the independence assumption identically in a worker; the worker
                // was simply the one measuring process that never said so.
                InstanceIndependence.Attach(
                    results,
                    InstanceIndependence.DependenceWarning(
                        suite.Type, suite.Lifetime, selected.Count, options));
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

        for (var index = 0; index < ordered.Count; index++)
        {
            var benchmark = ordered[index];
            var created = BenchmarkLifecycle.CreateInstance(suite.Type, instanceFactory, out var failure);

            if (created is null)
                return GroupOutcome.Failed(failure);

            var (instance, instanceTeardown) = created.Value;
            var instanceFromFactory = instanceFactory is not null;

            try
            {
                var singleBenchmarkSuite = suite with { Benchmarks = [benchmark] };
                var (setupSuccess, setupErrors) = BenchmarkLifecycle.TryRunSetup(singleBenchmarkSuite, instance, options);

                if (!setupSuccess)
                {
                    results.AddRange(setupErrors!);
                    continue;
                }

                var factory = () => instance;
                var envelope = BenchmarkEnvelope.FromDiscovered(benchmark, suite.Type.Name, factory);

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

}
