namespace NBenchmark.Stats;

public static class Percentile
{
    public static double Compute(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;

        if (sorted.Length == 1)
            return sorted[0];

        // The median (p == 0.50) uses the mid-average convention - the mean of the two middle
        // order statistics on even n - so it agrees with JitterCalibrator.Median and
        // LaunchAggregator.MedianOf, which already average the middles. Without this, the reported
        // Median had a small systematic downward bias on even n (nearest-rank picks the lower
        // middle). Every other percentile (Q1, Q3, P95, P99, ...) keeps the nearest-rank
        // convention below, which is deliberate and pinned by OutlierModeCrossCheckTests.
        if (Math.Abs(p - 0.50) < 1e-9)
        {
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
        }

        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }
}
