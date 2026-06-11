using System.Diagnostics;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     Composes the per-benchmark measurement math: trim the raw timings by the
///     configured <see cref="OutlierMode" />, compute the <see cref="StatsSummary" />,
///     and average allocation deltas. The single entry point the runner's success
///     path routes through.
/// </summary>
internal static class StatsPipeline
{
    public static ProcessedMeasurements Run(
        double[] rawTimings,
        long[]? rawAllocations,
        MeasurementOptions options)
    {
        // Preserve caller-visible RawSamples order; trim/sort works on a private copy.
        var timingsForStats = (double[])rawTimings.Clone();
        var (trimmed, discarded) = OutlierTrim.TrimDetailed(timingsForStats, options.OutlierMode);

        Debug.Assert(IsSorted(trimmed),
            "OutlierTrim must produce sorted output; Percentile.Compute requires sorted input.");

        var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
        long? meanAllocs = rawAllocations is not null ? ComputeMean(rawAllocations) : null;
        var warnings = BuildWarnings(trimmed, discarded, rawTimings.Length);

        return new ProcessedMeasurements(stats, trimmed.Length, meanAllocs) { Warnings = warnings };
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
    long? MeanAllocatedBytes)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
