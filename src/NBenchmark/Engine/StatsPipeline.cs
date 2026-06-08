using System.Diagnostics;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     Composes the per-benchmark measurement math: trim the raw timings by the
///     configured <see cref="OutlierMode" />, compute the <see cref="StatsSummary" />,
///     and average allocation deltas. The single entry point the runner's success
///     path routes through. Concentrates the trim → stats wiring the runner
///     previously owned.
/// </summary>
internal static class StatsPipeline
{
    public static ProcessedMeasurements Run(
        double[] rawTimings,
        long[]? rawAllocations,
        MeasurementOptions options)
    {
        var trimmed = OutlierTrim.Trim(rawTimings, options.OutlierMode);

        Debug.Assert(IsSorted(trimmed),
            "OutlierTrim must produce sorted output; Percentile.Compute requires sorted input.");

        var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
        long? meanAllocs = rawAllocations is not null ? (long)rawAllocations.Average() : null;

        return new ProcessedMeasurements(stats, trimmed.Length, meanAllocs);
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
