using NBenchmark.Diagnostics;
using NBenchmark.Engine.Detectors;

namespace NBenchmark.Engine;

internal static class SuiteRunner
{
    public static async Task<(List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples)> RunAsync(
        IReadOnlyList<BenchmarkEnvelope> envelopes,
        RunOrder order,
        int? seed,
        MeasurementOptions defaultOptions,
        int startIndex,
        int totalBenchmarks,
        IBenchmarkProgress progress,
        CancellationToken cancellationToken,
        Func<Task>? onBetweenBenchmarksAsync = null,
        IMeasurementObserver? observer = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(progress);

        var ordered = RunOrdering.Apply(envelopes, order, seed);

        var results = new List<BenchmarkResult>(ordered.Count);
        var rawSamples = new Dictionary<string, double[]>(ordered.Count);

        // The host drift canary. Readings bracket the benchmarks - one here, one at each boundary
        // after the inter-benchmark GC, one after the last - so benchmark i is bracketed by
        // readings i and i+1. Skipped for a dry-run, which measures nothing and should therefore
        // cost nothing.
        var canary = defaultOptions.Samples == 0
            ? null
            : HostDriftCanary.Create(defaultOptions.DriftCanary, clock ?? StopwatchClock.WallClock);

        canary?.Take();

        for (var index = 0; index < ordered.Count; index++)
        {
            var envelope = ordered[index];

            var spec = new RunSpec
            {
                Options = defaultOptions,
                Description = envelope.Description,
                IsBaseline = envelope.IsBaseline,
                Categories = envelope.Categories,
                Progress = progress,
                Observer = observer ?? NullMeasurementObserver.Instance,
            };

            await progress.OnBenchmarkStarting(envelope.Name, startIndex + index + 1, totalBenchmarks).ConfigureAwait(false);

            NBenchmarkDiagnostics.OnBenchmarkRunStarting(envelope.Name, envelope.ClassName, envelope.IsBaseline, envelope.ParameterSet);

            MeasurementOutcome outcome;
            BenchmarkResult completedResult;

            try
            {
                try
                {
                    outcome = await envelope.RunAsync(spec, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = OutcomeBuilder.Build(
                        new RunOutcome.Errored(ex),
                        envelope.Name,
                        envelope.ClassName,
                        envelope.Description,
                        envelope.IsBaseline,
                        spec.Options,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        0,
                        null,
                        envelope.Categories);
                }

                completedResult = outcome.Result with { ParameterSet = envelope.ParameterSet };

                results.Add(completedResult);
                rawSamples[envelope.Name] = outcome.RawSamples;

                NBenchmarkDiagnostics.OnBenchmarkRunCompleted(completedResult);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cancelledOutcome = OutcomeBuilder.Build(
                    new RunOutcome.Errored(new OperationCanceledException(cancellationToken), "Cancelled"),
                    envelope.Name,
                    envelope.ClassName,
                    envelope.Description,
                    envelope.IsBaseline,
                    spec.Options,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    null,
                    envelope.Categories);

                completedResult = cancelledOutcome.Result with { ParameterSet = envelope.ParameterSet };
                NBenchmarkDiagnostics.OnBenchmarkRunCompleted(completedResult);
                throw;
            }

            await progress.OnBenchmarkCompleted(completedResult).ConfigureAwait(false);

            if (ShouldForceGcBetweenBenchmarks(spec.Options, completedResult))
                GcControl.ForceFullGc();

            // Taken after the GC so a collection the benchmark just provoked is charged to that
            // benchmark rather than to the machine, and before the reset hook so user cleanup code
            // sits outside the bracket rather than inside it.
            canary?.Take();

            // PerClass shared-instance reset hook: fired once per gap, after this method's
            // completion + inter-benchmark GC and before the next method's OnBenchmarkStarting.
            // N-1 fires for N envelopes (no fire after the last benchmark; the first method sees
            // a fresh instance with setup already run). Null in per-method, per-benchmark, and
            // suite-mode paths.
            //
            // The gap between launches is deliberately not this callback's problem, and the
            // asymmetry is the contract rather than an omission: every caller builds a new instance
            // per launch, so the launch boundary already carries a fresh object and a re-run
            // [GlobalSetup]. Firing a reset there would ask a class to clean state that does not
            // exist yet.
            if (index < ordered.Count - 1 && onBetweenBenchmarksAsync is not null)
                await onBetweenBenchmarksAsync().ConfigureAwait(false);
        }

        StampHostTimeline(results, canary);

        return (results, rawSamples);
    }

    /// <summary>
    ///     Attaches each successful result's bracketing canary readings. Errored rows are skipped:
    ///     nothing was measured, so there is no measurement point to describe, and a stamp there
    ///     would invite a comparison against a row that has no number.
    /// </summary>
    private static void StampHostTimeline(List<BenchmarkResult> results, HostDriftCanary? canary)
    {
        if (canary is null)
            return;

        for (var index = 0; index < results.Count; index++)
        {
            if (results[index].Errored)
                continue;

            if (canary.StampFor(index) is { } stamp)
                results[index] = results[index] with { HostTimeline = stamp };
        }
    }

    private static bool ShouldForceGcBetweenBenchmarks(MeasurementOptions options, BenchmarkResult result)
    {
        if (!options.Resolve().ForceGcBetweenBenchmarks)
            return false;

        // True dry-runs do no work (0 warmup, 0 measured); skip inter-benchmark GC overhead.
        return result.WarmupSamples != 0 || result.SampleCount != 0 || result.Errored;
    }
}
