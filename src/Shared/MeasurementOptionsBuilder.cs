using NBenchmark.Engine;

namespace NBenchmark.Extensions.Abstractions;

internal static class MeasurementOptionsBuilder
{
    public static MeasurementOptions Build(IPerformanceThresholds thresholds)
    {
        var options = MeasurementOptions.Default;

        if (thresholds.Iterations > 0)
            options = options with { Iterations = thresholds.Iterations };
        if (thresholds.WarmupIterations > 0)
            options = options with { WarmupIterations = thresholds.WarmupIterations };
        if (thresholds.MeasureAllocations || thresholds.MaxAllocatedBytes >= 0)
            options = options with { MeasureAllocations = true };
        options = options with
        {
            OutlierMode = NormalizeOutlierMode(thresholds.OutlierMode),
            ConfidenceLevel = thresholds.ConfidenceLevel is > 0 and <= 1 ? thresholds.ConfidenceLevel : 0.95,
        };

        return options;
    }

    private static OutlierMode NormalizeOutlierMode(OutlierMode mode)
    {
        return mode is OutlierMode.None
            or OutlierMode.RemoveTop5Percent
            or OutlierMode.RemoveTopAndBottom5Percent
            or OutlierMode.IqrFence
            ? mode
            : OutlierMode.RemoveTop5Percent;
    }
}