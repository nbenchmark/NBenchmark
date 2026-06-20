namespace NBenchmark.Engine;

internal static class LaunchAggregator
{
    /// <summary>
    ///     Aggregates per-launch results into <see cref="LaunchStatistics" />.
    ///     Errored launches are excluded from statistics but recorded in
    ///     <see cref="LaunchDetail" />.
    /// </summary>
    public static LaunchStatistics Aggregate(IReadOnlyList<BenchmarkResult> launchResults, double confidenceLevel = 0.95)
    {
        ArgumentNullException.ThrowIfNull(launchResults);

        var successful = launchResults
            .Select((r, i) => (Result: r, Index: i))
            .Where(x => !x.Result.Errored)
            .ToList();

        var successfulMedians = successful.Select(x => x.Result.Median).ToArray();
        var successfulCount = successfulMedians.Length;

        var launchMean = successfulCount > 0 ? successfulMedians.Average() : 0;
        var launchMedian = successfulCount > 0 ? MedianOf(successfulMedians) : 0;

        var launchStdDev = successfulCount > 1
            ? Math.Sqrt(successfulMedians.Sum(m => (m - launchMean) * (m - launchMean)) / (successfulCount - 1))
            : 0;

        double? ciLower = null;
        double? ciUpper = null;

        if (successfulCount > 1)
        {
            var t = TValue(successfulCount - 1, confidenceLevel);
            var margin = t * launchStdDev / Math.Sqrt(successfulCount);
            ciLower = launchMean - margin;
            ciUpper = launchMean + margin;
        }

        var details = launchResults
            .Select((r, i) => new LaunchDetail
            {
                LaunchIndex = i,
                Median = r.Median,
                Mean = r.Mean,
                StandardDeviation = r.StandardDeviation,
                Iterations = r.MeasuredIterations,
                Duration = r.TotalDuration,
                Errored = r.Errored,
                ErrorMessage = r.ErrorMessage,
            })
            .ToList();

        return new LaunchStatistics
        {
            LaunchCount = successfulCount,
            LaunchMean = launchMean,
            LaunchStandardDeviation = launchStdDev,
            LaunchMedian = launchMedian,
            LaunchConfidenceIntervalLower = ciLower,
            LaunchConfidenceIntervalUpper = ciUpper,
            Launches = details,
        };
    }

    /// <summary>
    ///     Finds the best (lowest-median) result among the per-launch results.
    ///     Errored launches are excluded.
    /// </summary>
    public static BenchmarkResult BestLaunch(IReadOnlyList<BenchmarkResult> launchResults)
    {
        return launchResults
            .Where(r => !r.Errored)
            .OrderBy(r => r.Median)
            .FirstOrDefault(launchResults[0]);
    }

    private static double MedianOf(double[] values)
    {
        var sorted = values.Length switch
        {
            0 => Array.Empty<double>(),
            1 => values,
            _ => values.OrderBy(v => v).ToArray(),
        };

        if (sorted.Length == 0)
            return 0;

        var mid = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    ///     Returns the t-value for the given degrees of freedom and confidence level.
    ///     Uses a hardcoded table for small df, falls back to the normal approximation
    ///     (z-value) for df > 30.
    /// </summary>
    private static double TValue(int df, double confidenceLevel)
    {
        const double z95 = 1.96;
        const double z99 = 2.576;
        const double z90 = 1.645;
        const double z80 = 1.282;

        static double ZForConfidence(double cl)
        {
            return cl switch
            {
                >= 0.995 => 2.807,
                >= 0.99 => z99,
                >= 0.975 => 2.241,
                >= 0.95 => z95,
                >= 0.90 => z90,
                >= 0.80 => z80,
                _ => 1.0,
            };
        }

        if (df > 30)
            return ZForConfidence(confidenceLevel);

        // Two-tailed t-values for common confidence levels.
        // Key: df -> (80%, 90%, 95%, 99%, 99.5%)
        var z = ZForConfidence(confidenceLevel);

        // For a quick approximation with small df, we use the most common
        // two-tailed values at 95% confidence. Scale by z/z95 for other levels.
        ReadOnlySpan<double> t95 =
        [
            12.706, 4.303, 3.182, 2.776, 2.571,
            2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131,
            2.120, 2.110, 2.101, 2.093, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060,
            2.056, 2.052, 2.048, 2.045, 2.042,
        ];

        if (df >= 1 && df <= t95.Length)
        {
            var tAt95 = t95[df - 1];
            return tAt95 * (z / z95);
        }

        // Fallback for df beyond table but <= 30 (table covers 1-30)
        return z;
    }
}
