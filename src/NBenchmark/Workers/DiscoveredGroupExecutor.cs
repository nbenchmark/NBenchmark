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
        public static GroupOutcome Failed() => new([], [], true);
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
        var created = BenchmarkLifecycle.CreateInstance(suite.Type, instanceFactory);

        if (created is null)
            return GroupOutcome.Failed();

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

                Func<Task>? betweenBenchmarksReset = typeof(IStateReset).IsAssignableFrom(suite.Type)
                    ? () => ((IStateReset)instance).ResetAsync(cancellationToken)
                    : null;

                (results, samples) = await SuiteRunner.RunAsync(
                        envelopes, order, seed, options,
                        startIndex, totalBenchmarks, progress, cancellationToken,
                        betweenBenchmarksReset, observer)
                    .ConfigureAwait(false);
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
    ///     honoured here rather than inside <see cref="SuiteRunner" />, because each benchmark
    ///     gets its own single-envelope invocation.
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
        var ordered = Order(selected, order, seed);

        for (var index = 0; index < ordered.Count; index++)
        {
            var benchmark = ordered[index];
            var created = BenchmarkLifecycle.CreateInstance(suite.Type, instanceFactory);

            if (created is null)
                return GroupOutcome.Failed();

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

    /// <summary>
    ///     Applies run-order randomization. The previous isolated path hardcoded declaration
    ///     order, so <see cref="RunOrder.Random" /> was silently discarded whenever isolation was
    ///     on - which, in Harness mode, is always. Order is now honoured inside the measuring
    ///     process, where it belongs.
    /// </summary>
    private static List<BenchmarkMethodDefinition> Order(
        IReadOnlyList<BenchmarkMethodDefinition> benchmarks,
        RunOrder order,
        int? seed)
    {
        var items = benchmarks.ToList();

        if (order != RunOrder.Random || items.Count < 2)
            return items;

        var rng = new Random(seed ?? Random.Shared.Next());

        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }
}
