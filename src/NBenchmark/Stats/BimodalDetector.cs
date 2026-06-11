namespace NBenchmark.Stats;

/// <summary>
///     Inspects the slow samples an outlier trim discarded to decide whether they look
///     like a genuine secondary execution profile (a tight cluster taking a repeatable
///     amount of extra time - e.g. a cache miss forcing a full read, or lock contention)
///     rather than scattered OS scheduling noise. Random noise spreads delays across the
///     timeline; a structural bottleneck concentrates them, so a low relative spread in
///     the discarded tail is the tell.
/// </summary>
internal static class BimodalDetector
{
    // A discarded cluster must contain at least this many samples and at least this
    // fraction of the run before it is worth flagging.
    private const int MinClusterCount = 3;
    private const double MinClusterFraction = 0.01;

    // The discarded slow samples count as a "cluster" (rather than scatter) when their
    // coefficient of variation is below this - i.e. they took almost the same extra time.
    private const double MaxClusterSpread = 0.15;

    /// <summary>
    ///     Returns the slow discarded cluster (its sample count and centre) when the
    ///     trimmed-away upper tail forms a distinct, tight secondary peak; otherwise null.
    /// </summary>
    public static (int Count, double Center)? DetectSlowCluster(
        double[] kept,
        double[] discarded,
        int totalSamples)
    {
        if (kept.Length == 0 || discarded.Length == 0 || totalSamples == 0)
            return null;

        // kept is sorted ascending; its median is the centre of the main distribution.
        var keptMedian = Percentile.Compute(kept, 0.5);

        var count = 0;
        var sum = 0.0;

        foreach (var v in discarded)
        {
            if (v <= keptMedian)
                continue;

            count++;
            sum += v;
        }

        if (count < MinClusterCount || count < totalSamples * MinClusterFraction)
            return null;

        var mean = sum / count;

        if (mean <= 0)
            return null;

        var sumSquares = 0.0;

        foreach (var v in discarded)
        {
            if (v <= keptMedian)
                continue;

            var d = v - mean;
            sumSquares += d * d;
        }

        var stdDev = Math.Sqrt(sumSquares / count);
        var relativeSpread = stdDev / mean;

        return relativeSpread <= MaxClusterSpread ? (count, mean) : null;
    }
}
