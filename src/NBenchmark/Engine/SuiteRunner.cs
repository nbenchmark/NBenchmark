using System.Runtime.CompilerServices;
using NBenchmark.Diagnostics;

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
        IMeasurementObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(progress);

        var ordered = RunOrdering.Apply(envelopes, order, seed);

        var results = new List<BenchmarkResult>(ordered.Count);
        var rawSamples = new Dictionary<string, double[]>(ordered.Count);

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
                ForceFullGc();

            // PerClass shared-instance reset hook: fired once per gap, after this method's
            // completion + inter-benchmark GC and before the next method's OnBenchmarkStarting.
            // N-1 fires for N envelopes (no fire after the last benchmark; the first method sees
            // a fresh instance with setup already run). Null in per-method, per-benchmark, and
            // suite-mode paths.
            if (index < ordered.Count - 1 && onBetweenBenchmarksAsync is not null)
                await onBetweenBenchmarksAsync().ConfigureAwait(false);
        }

        return (results, rawSamples);
    }

    private static bool ShouldForceGcBetweenBenchmarks(MeasurementOptions options, BenchmarkResult result)
    {
        if (!options.ForceGcBetweenBenchmarks)
            return false;

        // True dry-runs do no work (0 warmup, 0 measured); skip inter-benchmark GC overhead.
        return result.WarmupIterations != 0 || result.MeasuredIterations != 0 || result.Errored;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceFullGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }
}
