namespace NBenchmark.Stats;

/// <summary>
///     A location-shift estimate between two samples, in the samples' own units (nanoseconds
///     per op here). <see cref="Value" /> is positive when the candidate is slower than the
///     baseline.
/// </summary>
/// <param name="Value">The Hodges-Lehmann point estimate (median of pairwise candidate − baseline differences).</param>
/// <param name="Lower">Lower confidence bound.</param>
/// <param name="Upper">Upper confidence bound.</param>
/// <param name="ConfidenceLevel">The coverage of <see cref="Lower" />..<see cref="Upper" /> (e.g. 0.95).</param>
public readonly record struct ShiftEstimate(double Value, double Lower, double Upper, double ConfidenceLevel);

/// <summary>
///     The Hodges-Lehmann shift estimator: the median of all pairwise differences between two
///     samples, with a rank-based (Lehmann) confidence interval. It is the standard
///     companion to the Mann-Whitney U test - where Cliff's delta says how <em>consistently</em>
///     the candidate is slower, this says <em>by how much</em>, in time units, with an interval.
/// </summary>
/// <remarks>
///     The pairwise-difference set is O(n₁·n₂); to bound cost each group is deterministically
///     stride-subsampled to at most <see cref="MaxPerGroup" /> values before the differences are
///     materialised, so a large auto-tuned run stays well under a million differences while the
///     estimate stays representative.
/// </remarks>
public static class HodgesLehmann
{
    /// <summary>The per-group cap applied by deterministic stride subsampling before pairing.</summary>
    public const int MaxPerGroup = 512;

    /// <summary>Minimum samples required in each group.</summary>
    private const int MinPerGroup = 2;

    /// <summary>
    ///     Estimates the candidate-minus-baseline shift and its confidence interval. Returns
    ///     <c>null</c> when either group has fewer than <see cref="MinPerGroup" /> samples or the
    ///     confidence level is out of range.
    /// </summary>
    /// <param name="baseline">Baseline raw samples (group A).</param>
    /// <param name="candidate">Candidate raw samples (group B).</param>
    /// <param name="confidenceLevel">Target coverage of the interval, strictly between 0 and 1.</param>
    public static ShiftEstimate? Estimate(double[] baseline, double[] candidate, double confidenceLevel)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (baseline.Length < MinPerGroup || candidate.Length < MinPerGroup
            || confidenceLevel is <= 0 or >= 1)
        {
            return null;
        }

        var a = Subsample(baseline, MaxPerGroup);
        var b = Subsample(candidate, MaxPerGroup);
        var n1 = a.Length;
        var n2 = b.Length;

        // All pairwise candidate - baseline differences. Positive = candidate slower.
        var diffs = new double[n1 * n2];
        var idx = 0;

        for (var j = 0; j < n2; j++)
        {
            var bj = b[j];

            for (var i = 0; i < n1; i++)
            {
                diffs[idx++] = bj - a[i];
            }
        }

        Array.Sort(diffs);

        var m = diffs.Length;
        var hl = MidMedian(diffs);

        // Lehmann large-sample interval: k = floor(mn/2 - z * sigma_U), with sigma_U the
        // tie-corrected Mann-Whitney standard deviation. The interval spans the k-th smallest
        // to the k-th largest difference, so it is symmetric about the median of differences and
        // excludes zero exactly when the U test rejects at alpha = 1 - confidenceLevel.
        var z = StudentT.NormalQuantile((1.0 + confidenceLevel) / 2.0);
        var sigma = Math.Sqrt(TieCorrectedVariance(a, b));
        var k = (int)Math.Floor(m / 2.0 - z * sigma);
        k = Math.Clamp(k, 1, m);

        var lower = diffs[k - 1];
        var upper = diffs[m - k];

        return new ShiftEstimate(hl, lower, upper, confidenceLevel);
    }

    /// <summary>
    ///     Deterministically reduces <paramref name="values" /> to at most <paramref name="max" />
    ///     entries by even striding over arrival order. Returns the input unchanged when it is
    ///     already within the cap.
    /// </summary>
    private static double[] Subsample(double[] values, int max)
    {
        if (values.Length <= max)
            return values;

        var stride = (double)values.Length / max;
        var result = new double[max];

        for (var i = 0; i < max; i++)
        {
            result[i] = values[(int)(i * stride)];
        }

        return result;
    }

    /// <summary>The mid-averaged median of a sorted array (mean of the two central values for even length).</summary>
    private static double MidMedian(double[] sorted)
    {
        var n = sorted.Length;

        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>
    ///     The tie-corrected variance of the Mann-Whitney U statistic for the two groups:
    ///     <c>n₁n₂/12 · [(N + 1) − Σ(tᵢ³ − tᵢ) / (N(N − 1))]</c>, where the tie blocks are computed
    ///     over the combined sample.
    /// </summary>
    private static double TieCorrectedVariance(double[] a, double[] b)
    {
        var n1 = a.Length;
        var n2 = b.Length;
        var total = n1 + n2;

        var combined = new double[total];
        Array.Copy(a, 0, combined, 0, n1);
        Array.Copy(b, 0, combined, n1, n2);
        Array.Sort(combined);

        var tieCorrection = 0.0;
        var j = 0;

        while (j < total)
        {
            var k = j + 1;

            while (k < total && combined[k] == combined[j])
            {
                k++;
            }

            var blockLength = k - j;

            if (blockLength > 1)
                tieCorrection += (double)blockLength * blockLength * blockLength - blockLength;

            j = k;
        }

        return (double)n1 * n2 / 12.0
               * (total + 1.0 - tieCorrection / ((double)total * (total - 1.0)));
    }
}
