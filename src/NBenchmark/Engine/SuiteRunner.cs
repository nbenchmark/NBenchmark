using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(progress);

        var ordered = order == RunOrder.Random
            ? Shuffle(envelopes.ToList(), seed ?? Random.Shared.Next())
            : envelopes.ToList();

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
                Progress = progress,
            };

            await progress.OnBenchmarkStarting(envelope.Name, startIndex + index + 1, totalBenchmarks).ConfigureAwait(false);

            MeasurementOutcome outcome;

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
                    envelope.Description,
                    envelope.IsBaseline,
                    spec.Options,
                    TimeSpan.Zero,
                    TimeSpan.Zero);
            }

            results.Add(outcome.Result);
            rawSamples[envelope.Name] = outcome.RawSamples;

            await progress.OnBenchmarkCompleted(outcome.Result).ConfigureAwait(false);

            if (ShouldForceGcBetweenBenchmarks(spec.Options, outcome.Result))
                ForceFullGc();
        }

        return (results, rawSamples);
    }

    public static async Task<(List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples)> RunIsolatedAsync(
        IReadOnlyList<BenchmarkEnvelope> envelopes,
        RunOrder order,
        MeasurementOptions defaultOptions,
        int startIndex,
        int totalBenchmarks,
        IBenchmarkProgress progress,
        string suiteName,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        int invocationOrdinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(progress);

        var ordered = order == RunOrder.Random
            ? Shuffle(envelopes.ToList(), Random.Shared.Next())
            : envelopes.ToList();

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
                Progress = progress,
            };

            await progress.OnBenchmarkStarting(envelope.Name, startIndex + index + 1, totalBenchmarks)
                .ConfigureAwait(false);

            var request = new IsolatedRunRequest
            {
                Mode = IsolatedRunMode.Suite,
                InvocationOrdinal = invocationOrdinal,
                CallerFilePath = callerFilePath,
                CallerLineNumber = callerLineNumber,
                CallerMemberName = callerMemberName,
                BenchmarkName = envelope.Name,
                SuiteName = suiteName,
                Options = spec.Options,
            };

            var outcome = await IsolatedRunContext
                .RunInIsolatedProcessAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var result = outcome.Result with
            {
                IsBaseline = spec.IsBaseline,
                Description = spec.Description,
            };

            results.Add(result);
            rawSamples[envelope.Name] = outcome.RawSamples;

            await progress.OnBenchmarkCompleted(result).ConfigureAwait(false);

            if (ShouldForceGcBetweenBenchmarks(spec.Options, result))
                ForceFullGc();
        }

        return (results, rawSamples);
    }

    private static List<BenchmarkEnvelope> Shuffle(List<BenchmarkEnvelope> items, int seed)
    {
        var rng = new Random(seed);
        var span = CollectionsMarshal.AsSpan(items);

        for (var i = span.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (span[i], span[j]) = (span[j], span[i]);
        }

        return items;
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
