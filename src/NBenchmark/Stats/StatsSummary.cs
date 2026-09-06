namespace NBenchmark.Stats;

internal sealed class StatsSummary
{
    public double MeanNs { get; init; }
    public double MedianNs { get; init; }
    public IReadOnlyList<PercentileEntry> Percentiles { get; init; } = [];
    public LatencyHistogram? Histogram { get; init; }
    public double MinNs { get; init; }
    public double MaxNs { get; init; }
    public double StandardDeviationNs { get; init; }

    /// <summary>
    ///     Standard error of the mean. <c>s / sqrt(n)</c> on an untrimmed set; the Winsorized (Yuen)
    ///     standard error <c>s_w × sqrt(n) / h</c> when outlier trimming removed samples, so the
    ///     variance the fence discarded is accounted for rather than dropped. See
    ///     <see cref="WinsorizedError" />.
    /// </summary>
    public double StandardErrorNs { get; init; }

    /// <summary>
    ///     Half-width of the confidence interval on the mean: <c>t* × StandardErrorNs</c>, read on
    ///     <c>n - 1</c> degrees of freedom on an untrimmed set and on <c>h - 1</c> when trimming
    ///     removed samples. See <see cref="StandardErrorNs" />.
    /// </summary>
    public double MarginOfErrorNs { get; init; }

    public double ConfidenceLevel { get; init; }

    public double CoefficientOfVariation { get; init; }

    public double Skewness { get; init; }
    public double Kurtosis { get; init; }
    public double MedianAbsoluteDeviationNs { get; init; }

    /// <summary>
    ///     Lower bound of the distribution-free confidence interval on the median (order-statistic
    ///     interval at <see cref="ConfidenceLevel" />). <c>null</c> when it is undefined (fewer than
    ///     two samples). Computed on the same set as <see cref="MedianNs" /> (the central,
    ///     trimmed set).
    /// </summary>
    public double? MedianConfidenceIntervalLowerNs { get; init; }

    /// <summary>Upper bound of the median confidence interval. <c>null</c> when undefined. See <see cref="MedianConfidenceIntervalLowerNs" />.</summary>
    public double? MedianConfidenceIntervalUpperNs { get; init; }

    /// <summary>
    ///     Computes the full descriptive-statistics summary for <paramref name="samples" />.
    ///     The input does not need to be pre-sorted: order-dependent statistics
    ///     (median, percentiles, min/max, MAD) are computed on a sorted copy when the
    ///     input is unsorted. The input array is never mutated.
    ///     <para>
    ///         When <paramref name="tailSource" /> is supplied, the order statistics that describe
    ///         the shape of the distribution - percentiles, min, max, and the histogram - are
    ///         computed from it instead of from <paramref name="samples" />, while every
    ///         central-tendency and dispersion statistic (mean, standard deviation, CI, CV,
    ///         skewness, kurtosis, MAD, median, and the median CI) stays on
    ///         <paramref name="samples" />. The engine passes the pre-trim set here so tail metrics
    ///         describe the full distribution while the central statistics stay robust to outliers.
    ///     </para>
    ///     <para>
    ///         When <paramref name="trim" /> is supplied and describes a set that actually lost
    ///         samples, <see cref="StandardErrorNs" /> and <see cref="MarginOfErrorNs" /> are the
    ///         Winsorized (Yuen) ones - see <see cref="WinsorizedError" /> - so the interval accounts
    ///         for the variance trimming removed instead of reporting the precision of a run that
    ///         happened to produce only the inliers. Every other statistic is unaffected. Omitting
    ///         the argument, or passing a context with nothing trimmed, leaves the plain
    ///         <c>s / sqrt(n)</c> interval exactly as it was.
    ///     </para>
    /// </summary>
    public static StatsSummary Compute(
        double[] samples,
        double confidenceLevel = 0.95,
        IReadOnlyList<double>? reportedPercentiles = null,
        bool enableHistogram = true,
        int histogramBucketCount = 20,
        double[]? tailSource = null,
        TrimContext? trim = null)
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

        // The tail source (when supplied) must be sorted too. Default it to the trimmed samples
        // so a null argument reproduces the pre-P2-1 all-from-trimmed behavior exactly.
        var tail = tailSource ?? samples;

        if (tail.Length == 0)
            tail = samples;
        else if (!ReferenceEquals(tail, samples) && !IsSorted(tail))
        {
            tail = (double[])tail.Clone();
            Array.Sort(tail);
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

        // The interval - and only the interval - moves onto the Winsorized (Yuen) estimator when the
        // caller says samples were trimmed away. The dispersion statistics below stay on the trimmed
        // set on purpose: a fenced-out spike must not move the standard deviation, the CV or the
        // shape moments, which is the whole reason the fence exists. What it must move is the claim
        // about how precisely the mean is known, because that claim is about the run and the run
        // produced n readings, not h. The short-circuit on an untrimmed context is deliberate rather
        // than an optimization: it keeps a clean run's margin bit-identical to the pre-Yuen value
        // instead of merely mathematically equal to it.
        if (trim is { IsTrimmed: true } context
            && WinsorizedError.Compute(context, confidenceLevel) is { } winsorized)
        {
            standardError = winsorized.StandardErrorNs;
            marginOfError = winsorized.MarginOfErrorNs;
        }

        var cv = mean != 0 ? sampleStdDev / mean : 0.0;

        var skewness = n > 2 && sampleStdDev > 0
            ? n * sumCube / ((n - 1.0) * (n - 2.0) * sampleStdDev * sampleStdDev * sampleStdDev)
            : 0.0;

        var kurtosis = n > 3 && sampleStdDev > 0
            ? n * (n + 1.0) * sumFourth / ((n - 1.0) * (n - 2.0) * (n - 3.0) * sampleStdDev * sampleStdDev * sampleStdDev * sampleStdDev)
              - 3.0 * (n - 1.0) * (n - 1.0) / ((n - 2.0) * (n - 3.0))
            : 0.0;

        var medianAbsoluteDeviation = ComputeMad(samples);

        // Order statistics describe the distribution's shape, so they read from the tail source
        // (the full pre-trim set by default). The central statistics above stay on the trimmed
        // samples so a fenced-out spike does not move the mean or inflate the CI.
        var percentiles = ComputePercentiles(tail, reportedPercentiles);
        var histogram = enableHistogram && tail.Length >= 2 ? ComputeHistogram(tail, histogramBucketCount) : null;
        var medianCi = MedianCi.Compute(samples, confidenceLevel);

        return new StatsSummary
        {
            MeanNs = mean,
            MedianNs = Percentile.Compute(samples, 0.50),
            Percentiles = percentiles,
            Histogram = histogram,
            MinNs = tail[0],
            MaxNs = tail[^1],
            StandardDeviationNs = sampleStdDev,
            StandardErrorNs = standardError,
            MarginOfErrorNs = marginOfError,
            ConfidenceLevel = confidenceLevel,
            CoefficientOfVariation = cv,
            Skewness = skewness,
            Kurtosis = kurtosis,
            MedianAbsoluteDeviationNs = medianAbsoluteDeviation,
            MedianConfidenceIntervalLowerNs = medianCi?.Lower,
            MedianConfidenceIntervalUpperNs = medianCi?.Upper,
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
        var medianAbsoluteDeviation = Percentile.Compute(absDiffs, 0.50);

        return medianAbsoluteDeviation * 1.4826;
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

internal readonly record struct AllocationStats(long Mean, long P50, long P95, long Max);
