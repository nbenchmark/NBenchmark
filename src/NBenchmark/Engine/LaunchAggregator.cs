using NBenchmark.Stats;

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
            // Use the same Student-t critical value the per-benchmark stats pipeline uses. The
            // previous implementation scaled a hard-coded 95% t-table by z/z95, which is wrong:
            // t-quantiles are not linear in z. The error was largest at low df and high confidence
            // (e.g. df=2, CL=0.99 returned 5.66 vs the true 9.925 - a 43% too-narrow CI).
            var t = StudentT.CriticalValue(confidenceLevel, successfulCount - 1);
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

        double? dispersion = null;
        double? varianceRatio = null;

        if (successfulCount > 1)
        {
            if (launchMean > 0)
                dispersion = launchStdDev / launchMean;

            // The typical within-process spread, as the mean of the per-launch standard deviations.
            // Comparing against the mean rather than the smallest keeps one unusually quiet launch
            // from making the whole run look irreproducible.
            var withinStdDev = successful.Average(x => x.Result.StandardDeviation);

            if (withinStdDev > 0)
                varianceRatio = launchStdDev / withinStdDev;
        }

        return new LaunchStatistics
        {
            LaunchCount = successfulCount,
            LaunchMean = launchMean,
            LaunchStandardDeviation = launchStdDev,
            LaunchMedian = launchMedian,
            LaunchConfidenceIntervalLower = ciLower,
            LaunchConfidenceIntervalUpper = ciUpper,
            BetweenLaunchDispersion = dispersion,
            ProcessVarianceRatio = varianceRatio,
            Launches = details,
        };
    }

    /// <summary>
    ///     The point past which the within-process confidence interval stops describing what a re-run
    ///     would produce.
    ///     <para>
    ///         Four is a judgement rather than a derived constant: below it, between- and
    ///         within-process spread are the same order of magnitude and the interval is a reasonable
    ///         guide; above it, run-to-run variation dominates and a tight interval is actively
    ///         misleading. It is deliberately generous, because a warning that fires on ordinary runs
    ///         teaches people to ignore it.
    ///     </para>
    /// </summary>
    internal const double ProcessVarianceWarningThreshold = 4.0;

    /// <summary>
    ///     A warning when run-to-run variation swamps the reported interval, or <c>null</c> when the
    ///     numbers reproduce well enough for the interval to mean what it appears to mean.
    ///     <para>
    ///         This exists because a p-value computed from samples pooled across processes inherits
    ///         the power of the pooled count, not the reproducibility of the measurement. With enough
    ///         samples, a difference far smaller than the run-to-run noise reads as overwhelmingly
    ///         significant. Saying so is the honest alternative to reporting a verdict the data
    ///         cannot support.
    ///     </para>
    /// </summary>
    public static string? DescribeReproducibility(LaunchStatistics? statistics)
    {
        if (statistics?.ProcessVarianceRatio is not { } ratio || ratio <= ProcessVarianceWarningThreshold)
            return null;

        var dispersion = statistics.BetweenLaunchDispersion is { } d
            ? $" Run-to-run spread is {d:P1} of the measurement."
            : "";

        return $"Run-to-run variation is {ratio:F0}x the within-run variation across "
               + $"{statistics.LaunchCount} launches, so the confidence interval on this row describes "
               + "precision within a single process rather than reproducibility."
               + dispersion
               + " Treat any significance verdict here as provisional, and compare the per-launch "
               + "medians instead. Raising --launch-count improves the reproducibility estimate.";
    }

    /// <summary>
    ///     Attaches launch statistics to the representative result, together with the
    ///     reproducibility warning when one applies.
    ///     <para>
    ///         Every path that aggregates launches goes through here, so the warning cannot be
    ///         present on one mode's output and missing from another's. Setting
    ///         <see cref="BenchmarkResult.LaunchStatistics" /> directly is the mistake this method
    ///         exists to prevent - it is exactly how the previous composite-key defect spread across
    ///         nine call sites that quietly disagreed.
    ///     </para>
    /// </summary>
    public static BenchmarkResult Apply(BenchmarkResult representative, LaunchStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(representative);
        ArgumentNullException.ThrowIfNull(statistics);

        var result = representative with { LaunchStatistics = statistics };

        return DescribeReproducibility(statistics) is { } warning
            ? result with { Warnings = [.. result.Warnings, warning] }
            : result;
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
}
