namespace NBenchmark.Stats;

public sealed class StatsSummary
{
    public double Mean { get; init; }
    public double Median { get; init; }
    public IReadOnlyList<PercentileEntry> Percentiles { get; init; } = [];
    public LatencyHistogram? Histogram { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double StandardDeviation { get; init; }

    public double StandardError { get; init; }

    public double MarginOfError { get; init; }

    public double ConfidenceLevel { get; init; }

    public double CoefficientOfVariation { get; init; }

    public double Skewness { get; init; }
    public double Kurtosis { get; init; }
    public double Mad { get; init; }

    /// <summary>
    ///     Computes the full descriptive-statistics summary for <paramref name="samples" />.
    ///     The input does not need to be pre-sorted: order-dependent statistics
    ///     (median, percentiles, min/max, MAD) are computed on a sorted copy when the
    ///     input is unsorted. The input array is never mutated.
    /// </summary>
    public static StatsSummary Compute(
        double[] samples,
        double confidenceLevel = 0.95,
        IReadOnlyList<double>? reportedPercentiles = null,
        bool enableHistogram = true,
        int histogramBucketCount = 20)
    {
        if (samples.Length == 0)
            return new StatsSummary { ConfidenceLevel = confidenceLevel };

        if (enableHistogram && histogramBucketCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(histogramBucketCount), histogramBucketCount,
                "Histogram bucket count must be at least 1 when histogram is enabled.");
        }

        // The engine always passes sorted (trimmed) samples, so the common case is a
        // single O(n) verification pass. Public callers may pass raw, unsorted samples
        // (e.g. MeasurementOutcome.RawSamples) - sort a copy so every order-dependent
        // statistic stays correct.
        if (!IsSorted(samples))
        {
            samples = (double[])samples.Clone();
            Array.Sort(samples);
        }

        var n = samples.Length;
        var sum = 0.0;

        for (var i = 0; i < n; i++)
        {
            sum += samples[i];
        }

        var mean = sum / n;

        var sumSq = 0.0;
        var sumCube = 0.0;
        var sumFourth = 0.0;

        for (var i = 0; i < n; i++)
        {
            var d = samples[i] - mean;
            sumSq += d * d;
            sumCube += d * d * d;
            sumFourth += d * d * d * d;
        }

        var sampleStdDev = n > 1 ? Math.Sqrt(sumSq / (n - 1)) : 0.0;
        var standardError = n > 1 ? sampleStdDev / Math.Sqrt(n) : 0.0;

        var marginOfError = 0.0;

        if (n > 1)
        {
            var tCritical = StudentT.CriticalValue(confidenceLevel, n - 1);

            if (!double.IsNaN(tCritical))
                marginOfError = tCritical * standardError;
        }

        var cv = mean != 0 ? sampleStdDev / mean : 0.0;

        var skewness = n > 2 && sampleStdDev > 0
            ? n * sumCube / ((n - 1.0) * (n - 2.0) * sampleStdDev * sampleStdDev * sampleStdDev)
            : 0.0;

        var kurtosis = n > 3 && sampleStdDev > 0
            ? n * (n + 1.0) * sumFourth / ((n - 1.0) * (n - 2.0) * (n - 3.0) * sampleStdDev * sampleStdDev * sampleStdDev * sampleStdDev)
              - 3.0 * (n - 1.0) * (n - 1.0) / ((n - 2.0) * (n - 3.0))
            : 0.0;

        var mad = ComputeMad(samples);

        var percentiles = ComputePercentiles(samples, reportedPercentiles);
        var histogram = enableHistogram && n >= 2 ? ComputeHistogram(samples, histogramBucketCount) : null;

        return new StatsSummary
        {
            Mean = mean,
            Median = Percentile.Compute(samples, 0.50),
            Percentiles = percentiles,
            Histogram = histogram,
            Min = samples[0],
            Max = samples[^1],
            StandardDeviation = sampleStdDev,
            StandardError = standardError,
            MarginOfError = marginOfError,
            ConfidenceLevel = confidenceLevel,
            CoefficientOfVariation = cv,
            Skewness = skewness,
            Kurtosis = kurtosis,
            Mad = mad,
        };
    }

    private static IReadOnlyList<PercentileEntry> ComputePercentiles(
        double[] sorted, IReadOnlyList<double>? requested)
    {
        var normalized = MeasurementOptions.NormalizePercentiles(
            requested ?? MeasurementOptions.DefaultReportedPercentiles);

        if (normalized.Count == 0)
            return Array.Empty<PercentileEntry>();

        var entries = new List<PercentileEntry>(normalized.Count);

        foreach (var p in normalized)
        {
            var value = p >= 1.0 ? sorted[^1] : Percentile.Compute(sorted, p);
            entries.Add(new PercentileEntry(p, value));
        }

        return entries.ToArray();
    }

    internal static LatencyHistogram ComputeHistogram(double[] sorted, int bucketCount)
    {
        var min = sorted[0];
        var max = sorted[^1];

        if (Math.Abs(max - min) < 1e-9)
        {
            return new LatencyHistogram(
                [new HistogramBucket(min, max, sorted.Length)],
                min, max, sorted.Length);
        }

        var bucketWidth = (max - min) / bucketCount;
        var buckets = new HistogramBucket[bucketCount];

        for (var i = 0; i < bucketCount; i++)
        {
            var lower = min + i * bucketWidth;
            var upper = i == bucketCount - 1 ? max : min + (i + 1) * bucketWidth;
            buckets[i] = new HistogramBucket(lower, upper, 0);
        }

        foreach (var sample in sorted)
        {
            var idx = (int)((sample - min) / bucketWidth);
            idx = Math.Clamp(idx, 0, bucketCount - 1);
            buckets[idx] = buckets[idx] with { Count = buckets[idx].Count + 1 };
        }

        return new LatencyHistogram(buckets, min, max, sorted.Length);
    }

    private static bool IsSorted(double[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
                return false;
        }

        return true;
    }

    private static double ComputeMad(double[] sorted)
    {
        if (sorted.Length == 0)
            return 0;

        var median = Percentile.Compute(sorted, 0.50);
        var absDiffs = new double[sorted.Length];

        for (var i = 0; i < sorted.Length; i++)
        {
            absDiffs[i] = Math.Abs(sorted[i] - median);
        }

        Array.Sort(absDiffs);
        var mad = Percentile.Compute(absDiffs, 0.50);

        return mad * 1.4826;
    }

    public static AllocationStats ComputeAllocations(long[]? samples)
    {
        if (samples is null || samples.Length == 0)
            return new AllocationStats(0, 0, 0, 0);

        var sorted = (long[])samples.Clone();
        Array.Sort(sorted);

        double sum = 0;

        for (var i = 0; i < sorted.Length; i++)
        {
            sum += sorted[i];
        }

        var asDoubles = Array.ConvertAll(sorted, v => (double)v);
        var mean = (long)(sum / sorted.Length);
        var p50 = Percentile.Compute(asDoubles, 0.50);
        var p95 = Percentile.Compute(asDoubles, 0.95);
        var max = sorted[^1];

        return new AllocationStats(mean, (long)p50, (long)p95, max);
    }
}

public readonly record struct AllocationStats(long Mean, long P50, long P95, long Max);
