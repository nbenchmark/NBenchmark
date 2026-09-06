namespace NBenchmark.Stats;

/// <summary>
///     A distribution-free confidence interval for the population median, built from order
///     statistics of the sample. Unlike the t-interval on the mean, it makes no normality
///     assumption - the median is the headline comparison metric (ratios and the
///     Mann-Whitney-adjacent semantics both key off it), so a matching assumption-free
///     interval closes the "we stop on the mean's CI but compare medians" gap.
/// </summary>
/// <remarks>
///     <para>
///         For <c>n &lt; 50</c> the exact rank bounds come from the binomial(<c>n</c>, ½)
///         distribution: the interval <c>[X(l), X(u)]</c> (1-based order statistics) covers the
///         median with probability <c>1 − 2·CDF(l−1)</c>, and <c>l</c> is the largest rank whose
///         lower-tail mass does not exceed <c>α/2</c>. This guarantees coverage at least the
///         requested level (it can only be conservative, never anti-conservative).
///     </para>
///     <para>
///         For <c>n ≥ 50</c> the normal approximation to the binomial gives
///         <c>l = ⌊(n − z·√n)/2⌋</c> and <c>u = ⌈1 + (n + z·√n)/2⌉</c>, with
///         <c>z = Φ⁻¹((1 + CL)/2)</c>. Ranks are clamped into <c>[1, n]</c>; when even the widest
///         interval <c>[X(1), X(n)]</c> cannot reach the requested level (tiny <c>n</c>, high
///         <c>CL</c>) the full range is returned - the honest widest bound the data supports.
///     </para>
/// </remarks>
internal static class MedianCi
{
    /// <summary>Above this sample size the normal approximation replaces the exact binomial search.</summary>
    private const int ExactMaxSamples = 50;

    /// <summary>
    ///     Computes the median confidence interval from a sorted sample. Returns <c>null</c> when
    ///     the interval is undefined (<c>n &lt; 2</c> or an out-of-range confidence level).
    /// </summary>
    /// <param name="sorted">Samples sorted ascending. Not mutated.</param>
    /// <param name="confidenceLevel">The target coverage, strictly between 0 and 1 (e.g. 0.95).</param>
    public static (double Lower, double Upper)? Compute(double[] sorted, double confidenceLevel)
    {
        ArgumentNullException.ThrowIfNull(sorted);

        var n = sorted.Length;

        if (n < 2 || confidenceLevel is <= 0 or >= 1)
            return null;

        int lowerRank; // 1-based
        int upperRank; // 1-based

        if (n < ExactMaxSamples)
        {
            var target = (1.0 - confidenceLevel) / 2.0;

            // Largest 1-based rank l with CDF(l-1; n, 0.5) <= alpha/2. pmf is walked
            // iteratively (pmf(k+1) = pmf(k)·(n-k)/(k+1)) to avoid binomial-coefficient overflow.
            var pmf = Math.Pow(0.5, n);
            var cdf = 0.0;
            var l = 0;

            for (var k = 0; k <= n; k++)
            {
                cdf += pmf;

                if (cdf <= target)
                    l = k + 1;
                else
                    break;

                pmf *= (double)(n - k) / (k + 1);
            }

            lowerRank = l;
            upperRank = n + 1 - l;
        }
        else
        {
            var z = StudentT.NormalQuantile((1.0 + confidenceLevel) / 2.0);
            var spread = z * Math.Sqrt(n);
            lowerRank = (int)Math.Floor((n - spread) / 2.0);
            upperRank = (int)Math.Ceiling(1.0 + (n + spread) / 2.0);
        }

        lowerRank = Math.Clamp(lowerRank, 1, n);
        upperRank = Math.Clamp(upperRank, 1, n);

        if (upperRank < lowerRank)
            (lowerRank, upperRank) = (upperRank, lowerRank);

        return (sorted[lowerRank - 1], sorted[upperRank - 1]);
    }
}
