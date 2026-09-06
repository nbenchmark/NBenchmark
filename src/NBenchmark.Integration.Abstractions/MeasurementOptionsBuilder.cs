namespace NBenchmark.Integration.Abstractions;

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
            options = options with { MeasureAllocationsOverride = true };

        options = options with
        {
            OutlierMode = NormalizeOutlierMode(thresholds.OutlierMode),
            ConfidenceLevel = thresholds.ConfidenceLevel is > 0 and <= 1 ? thresholds.ConfidenceLevel : 0.95,
        };

        return options;
    }

    /// <summary>
    ///     The replicate count the attribute asked for, spent by <c>TestMethodRunner</c> launching one
    ///     worker per launch.
    /// </summary>
    /// <remarks>
    ///     Returned separately from <see cref="Build" /> rather than as a field on
    ///     <see cref="MeasurementOptions" />, because a launch is a process and the options are handed
    ///     to each of them - see <see cref="LaunchCounts" />. Clamped rather than validated: an
    ///     attribute is a compile-time constant, and throwing here would fail the test with a
    ///     configuration error instead of measuring it.
    /// </remarks>
    public static int LaunchCount(IPerformanceThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        return LaunchCounts.Clamp(thresholds.LaunchCount);
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
