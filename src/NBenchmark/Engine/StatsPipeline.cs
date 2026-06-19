using System.Diagnostics;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

internal static class StatsPipeline
{
    public static ProcessedMeasurements Run(
        double[] rawTimings,
        long[]? rawAllocations,
        MeasurementOptions options)
    {
        var timingsForStats = (double[])rawTimings.Clone();
        var trimResult = OutlierTrim.TrimDetailed(timingsForStats, options.ResolveOutlierDetector());

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
                rawAllocations)
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

internal sealed record ProcessedMeasurements(
    StatsSummary Stats,
    int MeasuredIterations,
    long? MeanAllocatedBytes,
    double Q1,
    double Q3,
    double InterquartileRange,
    double? LowerFence,
    double? UpperFence,
    int OutliersRemoved,
    long[]? RawAllocations)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
