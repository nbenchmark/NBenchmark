namespace NBenchmark.Integration.Abstractions;

public static class MeasurementOptionsBuilder
{
    public static MeasurementOptions Build(IPerformanceThresholds thresholds)
    {
        var options = MeasurementOptions.Default;

        if (thresholds.Iterations > 0)
            options = options with { Iterations = thresholds.Iterations };

        if (thresholds.WarmupIterations > 0)
            options = options with { WarmupIterations = thresholds.WarmupIterations };

        if (thresholds.MeasureAllocations || thresholds.MaxAllocatedBytes >= 0)
            options = options with { MeasureAllocationsOverride = true };

        options = options with
        {
            OutlierMode = NormalizeOutlierMode(thresholds.OutlierMode),
            ConfidenceLevel = thresholds.ConfidenceLevel is > 0 and <= 1 ? thresholds.ConfidenceLevel : 0.95,

            // The replicate count, spent by TestMethodRunner spawning one worker per launch. Clamped
            // rather than validated: an attribute is a compile-time constant, and throwing from an
            // options builder would fail the test with a configuration error instead of measuring it.
            LaunchCount = Math.Clamp(thresholds.LaunchCount, 1, MeasurementOptions.MaxLaunchCount),
        };

        return options;
    }

    private static OutlierMode NormalizeOutlierMode(OutlierMode mode)
    {
        return mode is OutlierMode.None
            or OutlierMode.RemoveTop5Percent
            or OutlierMode.RemoveTopAndBottom5Percent
            or OutlierMode.IqrFence
            or OutlierMode.MedianAbsoluteDeviation
            ? mode
            : OutlierMode.IqrFence;
    }
}
