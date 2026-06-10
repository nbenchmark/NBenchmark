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
        var trimmed = OutlierTrim.Trim(timingsForStats, options.OutlierMode);

        Debug.Assert(IsSorted(trimmed),
            "OutlierTrim must produce sorted output; Percentile.Compute requires sorted input.");

        var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
        long? meanAllocs = rawAllocations is not null ? ComputeMean(rawAllocations) : null;

        return new ProcessedMeasurements(stats, trimmed.Length, meanAllocs);
    }

    private static long ComputeMean(long[] values)
    {
        double sum = 0;
        for (var i = 0; i < values.Length; i++)
            sum += values[i];
        return (long)(sum / values.Length);
    }

    private static bool IsSorted(double[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1]) return false;
        }
        return true;
    }
}

internal sealed record ProcessedMeasurements(
    StatsSummary Stats,
    int MeasuredIterations,
    long? MeanAllocatedBytes);
