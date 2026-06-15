namespace NBenchmark.Stats;

/// <summary>
///     Two-sided Mann-Whitney U test.
///     <para>
///         For small, tie-free samples (combined size &lt;= <see cref="ExactMaxCombinedSamples" />)
///         the exact null distribution of <c>U</c> is enumerated via a bounded-partition
///         recurrence, giving the same p-value as a full permutation test but in
///         polynomial time. The central-limit normal approximation does not hold at small
///         <c>n</c> (it can drift from the exact value by up to ~0.05 near <c>n = 5</c>),
///         which would let a benchmark earn a passing significance verdict for the wrong
///         reason - so we never use it there.
///     </para>
///     <para>
///         For larger samples - or whenever ties are present, which break the exact
///         distribution's assumptions - the test falls back to the asymptotic normal
///         approximation with a tie correction and a continuity correction. At those sizes
///         the approximation error is mathematically negligible.
///     </para>
/// </summary>
public static class MannWhitneyU
{
    /// <summary>
    ///     Inclusive upper bound on the combined sample size (<c>n1 + n2</c>) for which the
    ///     exact distribution is enumerated. Above this, the asymptotic approximation is used.
    /// </summary>
    public const int ExactMaxCombinedSamples = 20;

    /// <summary>Minimum samples required in each group to attempt the test.</summary>
    private const int MinPerGroup = 2;

    public static MannWhitneyUResult Test(double[] sampleA, double[] sampleB)
    {
        var n1 = sampleA.Length;
        var n2 = sampleB.Length;

        if (n1 < MinPerGroup || n2 < MinPerGroup)
            return new MannWhitneyUResult(double.NaN, double.NaN);

        var combined = new (double Value, int Group)[n1 + n2];

        for (var i = 0; i < n1; i++)
        {
            combined[i] = (sampleA[i], 0);
        }

        for (var i = 0; i < n2; i++)
        {
            combined[n1 + i] = (sampleB[i], 1);
        }

        Array.Sort(combined, (a, b) => a.Value.CompareTo(b.Value));

        var ranks = new double[n1 + n2];
        var hasTies = false;
        var j = 0;

        while (j < combined.Length)
        {
            var k = j + 1;

            while (k < combined.Length && combined[k].Value == combined[j].Value)
            {
                k++;
            }

            if (k - j > 1)
                hasTies = true;

            var meanRank = (j + k + 1) / 2.0;

            for (var t = j; t < k; t++)
            {
                ranks[t] = meanRank;
            }

            j = k;
        }

        double r1 = 0;

        for (var i = 0; i < combined.Length; i++)
        {
            if (combined[i].Group == 0)
                r1 += ranks[i];
        }

        var u1 = r1 - (double)n1 * (n1 + 1) / 2.0;
        var u2 = (double)n1 * n2 - u1;
        // Cliff's delta: δ = P(B > A) - P(B < A) = (U2 - U1) / (n1 * n2).
        // Positive δ means the candidate (group B) tends to be larger (slower) than
        // the baseline (group A).
        var cliffsDelta = (u2 - u1) / ((double)n1 * n2);

        // Exact enumeration is only valid without ties (mid-ranks shift the discrete
        // distribution). For small, tie-free samples it is both exact and cheap.
        if (!hasTies && n1 + n2 <= ExactMaxCombinedSamples)
        {
            var pValue = ExactTwoSided(n1, n2, u1);
            return new MannWhitneyUResult(pValue, cliffsDelta);
        }

        var asymptoticPValue = AsymptoticTwoSided(combined, n1, n2, u1);
        return new MannWhitneyUResult(asymptoticPValue, cliffsDelta);
    }

    /// <summary>
    ///     Exact two-sided p-value: the fraction of all <c>C(n1+n2, n1)</c> rank
    ///     assignments whose <c>U</c> statistic is at least as far from the mean as the
    ///     observed one. Mirrors SciPy's exact method.
    /// </summary>
    private static double ExactTwoSided(int n1, int n2, double u1Observed)
    {
        var counts = ExactCounts(n1, n2);
        var maxU = n1 * n2;
        var mu = maxU / 2.0;
        var distance = Math.Abs(u1Observed - mu);

        double total = 0;
        double extreme = 0;

        for (var u = 0; u <= maxU; u++)
        {
            total += counts[u];

            if (Math.Abs(u - mu) >= distance - 1e-9)
                extreme += counts[u];
        }

        return total == 0 ? double.NaN : Math.Min(1.0, extreme / total);
    }

    /// <summary>
    ///     Number of rank assignments yielding each value of <c>U</c> (index = U, range
    ///     <c>0..n1*n2</c>), via the recurrence
    ///     <c>c(i,j,u) = c(i-1,j,u-j) + c(i,j-1,u)</c>. Summing the result gives
    ///     <c>C(n1+n2, n1)</c>.
    /// </summary>
    private static double[] ExactCounts(int n1, int n2)
    {
        var prev = new double[n2 + 1][];

        for (var jj = 0; jj <= n2; jj++)
        {
            prev[jj] = [1.0];
        }

        for (var i = 1; i <= n1; i++)
        {
            var cur = new double[n2 + 1][];
            cur[0] = [1.0];

            for (var jj = 1; jj <= n2; jj++)
            {
                var max = i * jj;
                var arr = new double[max + 1];
                var fromPrev = prev[jj];
                var fromLeft = cur[jj - 1];

                for (var u = 0; u <= max; u++)
                {
                    var a = u - jj >= 0 && u - jj < fromPrev.Length ? fromPrev[u - jj] : 0;
                    var b = u < fromLeft.Length ? fromLeft[u] : 0;
                    arr[u] = a + b;
                }

                cur[jj] = arr;
            }

            prev = cur;
        }

        return prev[n2];
    }

    private static double AsymptoticTwoSided(
        (double Value, int Group)[] combined,
        int n1,
        int n2,
        double u1)
    {
        var u2 = (double)n1 * n2 - u1;
        var u = Math.Min(u1, u2);

        var mu = (double)n1 * n2 / 2.0;
        var total = n1 + n2;

        var tieCorrection = 0.0;
        var j = 0;

        while (j < combined.Length)
        {
            var k = j + 1;

            while (k < combined.Length && combined[k].Value == combined[j].Value)
            {
                k++;
            }

            var t = k - j;

            if (t > 1)
                tieCorrection += (double)t * t * t - t;

            j = k;
        }

        var sigma = Math.Sqrt(
            (double)n1 * n2 / (total * (total - 1)) *
            ((total * total * total - total) / 12.0 - tieCorrection / 12.0)
        );

        if (sigma == 0)
            return 1.0;

        // Continuity correction: shrink the gap by 0.5 (a discrete U approximated by a
        // continuous normal). Clamp so an exactly-central U yields p = 1.
        var corrected = Math.Max(0.0, Math.Abs(u - mu) - 0.5);
        var z = corrected / sigma;

        return 2.0 * (1.0 - NormalCdf(z));
    }

    private static double NormalCdf(double x)
    {
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x) / Math.Sqrt(2.0);

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}

/// <summary>
///     The result of a two-sided Mann-Whitney U test, including the p-value and
///     Cliff's delta effect size.
/// </summary>
/// <param name="PValue">The two-sided p-value, or <c>NaN</c> when the test could not run.</param>
/// <param name="CliffsDelta">
///     Cliff's delta: <c>(U2 - U1) / (n1 * n2)</c>, equivalent to
///     <c>P(B &gt; A) - P(B &lt; A)</c> with A = baseline, B = candidate. Positive means
///     samples in group B (the candidate) tend to be larger (slower) than group A (the
///     baseline). <c>NaN</c> when the test could not run.
/// </param>
public readonly record struct MannWhitneyUResult(double PValue, double CliffsDelta);
