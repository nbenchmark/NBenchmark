namespace NBenchmark.Stats;

public sealed class StatsSummary
{
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
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
    public static StatsSummary Compute(double[] samples, double confidenceLevel = 0.95)
    {
        if (samples.Length == 0)
            return new StatsSummary { ConfidenceLevel = confidenceLevel };

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
            ? (n * sumCube) / ((n - 1.0) * (n - 2.0) * sampleStdDev * sampleStdDev * sampleStdDev)
            : 0.0;
        var kurtosis = n > 3 && sampleStdDev > 0
            ? (n * (n + 1.0) * sumFourth) / ((n - 1.0) * (n - 2.0) * (n - 3.0) * sampleStdDev * sampleStdDev * sampleStdDev * sampleStdDev)
              - (3.0 * (n - 1.0) * (n - 1.0)) / ((n - 2.0) * (n - 3.0))
            : 0.0;

        var mad = ComputeMad(samples);

        return new StatsSummary
        {
            Mean = mean,
            Median = Percentile.Compute(samples, 0.50),
            P95 = Percentile.Compute(samples, 0.95),
            P99 = Percentile.Compute(samples, 0.99),
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
            sum += sorted[i];

        var asDoubles = Array.ConvertAll(sorted, v => (double)v);
        var mean = (long)(sum / sorted.Length);
        var p50 = Percentile.Compute(asDoubles, 0.50);
        var p95 = Percentile.Compute(asDoubles, 0.95);
        var max = sorted[^1];

        return new AllocationStats(mean, (long)p50, (long)p95, max);
    }
}

public readonly record struct AllocationStats(long Mean, long P50, long P95, long Max);