using System.Diagnostics;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     The full trim -> summary -> warnings pipeline that turns raw per-op timings and
///     allocation deltas into a <see cref="ProcessedMeasurements" />. Exposed so an
///     external consumer that has captured raw samples (for example NBenchmark.Studio,
///     ingesting OTLP) can run exactly the same statistical processing the engine uses,
///     without reimplementing outlier trimming and warning generation.
/// </summary>
public static class StatsPipeline
{
    public static ProcessedMeasurements Run(
        double[] rawTimings,
        long[]? rawAllocations,
        MeasurementOptions options)
    {
        // OutlierTrim.TrimDetailed clones internally and does not mutate the input, so
        // rawTimings stays in arrival order and TrimmedOrdinals map back correctly.
        var trimResult = OutlierTrim.TrimDetailed(rawTimings, options.ResolveOutlierDetector());

        Debug.Assert(IsSorted(trimResult.Kept),
            "OutlierTrim must produce sorted output; Percentile.Compute requires sorted input.");

        var stats = StatsSummary.Compute(
            trimResult.Kept,
            options.ConfidenceLevel,
            options.ReportedPercentiles,
            options.EnableHistogram,
            options.HistogramBucketCount);

        long? meanAllocs = rawAllocations is not null ? ComputeMean(rawAllocations) : null;
        var warnings = BuildWarnings(trimResult.Kept, trimResult.Discarded, rawTimings.Length);
        var outliersRemoved = rawTimings.Length - trimResult.Kept.Length;

        return new ProcessedMeasurements(
                stats,
                trimResult.Kept.Length,
                meanAllocs,
                trimResult.Q1,
                trimResult.Q3,
                trimResult.InterquartileRange,
                trimResult.LowerFence,
                trimResult.UpperFence,
                outliersRemoved,
                rawAllocations,
                trimResult.TrimmedOrdinals)
            { Warnings = warnings };
    }

    private static IReadOnlyList<string> BuildWarnings(double[] trimmed, double[] discarded, int totalSamples)
    {
        var cluster = BimodalDetector.DetectSlowCluster(trimmed, discarded, totalSamples);

        if (cluster is null)
            return [];

        var (count, center) = cluster.Value;

        return
        [
            $"{count} discarded outlier(s) form a distinct cluster near {BenchmarkFormatter.FormatNs(center)} "
            + "rather than scattered noise - possible bimodal distribution; investigate this tail latency "
            + "(e.g. GC pauses, lock contention, or cache misses).",
        ];
    }

    private static long ComputeMean(long[] values)
    {
        double sum = 0;

        for (var i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return (long)(sum / values.Length);
    }

    private static bool IsSorted(double[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
                return false;
        }

        return true;
    }
}

/// <summary>
///     The processed output of <see cref="StatsPipeline.Run" />: the summary statistics, the
///     measured (post-trim) iteration count, mean allocations, the quartile statistics and
///     fence boundaries, the outlier count, the raw allocation samples, the original
///     ordinals of every trimmed sample, and any warnings the pipeline produced.
/// </summary>
public sealed record ProcessedMeasurements(
    StatsSummary Stats,
    int MeasuredIterations,
    long? MeanAllocatedBytes,
    double Q1,
    double Q3,
    double InterquartileRange,
    double? LowerFence,
    double? UpperFence,
    int OutliersRemoved,
    long[]? RawAllocations,
    int[] TrimmedOrdinals)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DiagnosticsResult? DiagnosticsResult { get; init; }
}
