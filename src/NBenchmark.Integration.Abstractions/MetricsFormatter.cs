namespace NBenchmark.Integration.Abstractions;

internal static class MetricsFormatter
{
    public static string Format(BenchmarkResult result)
    {
        var allocations = result.AllocatedBytesMean.HasValue
            ? $"{result.AllocatedBytesMean.Value} B"
            : "n/a";

        var p95 = result.GetPercentile(0.95);
        var p95Text = p95.HasValue ? $"{p95.Value:F2} ns" : "n/a";

        return
            $"NBenchmark metrics{Environment.NewLine}" +
            $"MeanNs: {result.MeanNs:F2} ns{Environment.NewLine}" +
            $"P95: {p95Text}{Environment.NewLine}" +
            $"Allocations: {allocations}{Environment.NewLine}" +
            $"Samples: {result.SampleCount} (warmup: {result.WarmupSamples})" +
            Launches(result);
    }

    /// <summary>
    ///     The replicate line, when the test asked for replicates.
    /// </summary>
    /// <remarks>
    ///     Printed because the numbers above it mean something different once there are replicates: the
    ///     mean is averaged over the launches and its interval is the spread <i>between</i> them rather
    ///     than within one. A reader who cannot see how many launches produced the row cannot tell which
    ///     of the two they are looking at.
    /// </remarks>
    private static string Launches(BenchmarkResult result)
    {
        if (result.LaunchStatistics is not { LaunchCount: > 1 } launches)
            return "";

        var spread = launches.BetweenLaunchDispersion is { } dispersion
            ? $", run-to-run spread {dispersion:P1}"
            : "";

        return $"{Environment.NewLine}Launches: {launches.LaunchCount} worker(s), median "
               + $"{launches.LaunchMedian:F2} ns{spread}";
    }
}
