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
}
