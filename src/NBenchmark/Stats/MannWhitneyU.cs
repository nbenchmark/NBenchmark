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
        ArgumentNullException.ThrowIfNull(sampleA);
        ArgumentNullException.ThrowIfNull(sampleB);

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
        var tieCorrection = 0.0;
        var j = 0;

        while (j < combined.Length)
        {
            var k = j + 1;

            while (k < combined.Length && combined[k].Value == combined[j].Value)
            {
                k++;
            }

            var blockLength = k - j;

            if (blockLength > 1)
            {
                hasTies = true;
                tieCorrection += (double)blockLength * blockLength * blockLength - blockLength;
            }

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

        var asymptoticPValue = AsymptoticTwoSided(n1, n2, u1, tieCorrection);
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
        int n1,
        int n2,
        double u1,
        double tieCorrection)
    {
        var u2 = (double)n1 * n2 - u1;
        var u = Math.Min(u1, u2);

        var mu = (double)n1 * n2 / 2.0;
        var total = n1 + n2;

        // Numerically stable tie-corrected variance:
        // n1*n2/12 * [ (N + 1) - sum(t^3 - t)/(N*(N - 1)) ].
        var variance =
            (double)n1 * n2 / 12.0 *
            (total + 1.0 - tieCorrection / (total * (total - 1.0)));

        if (variance <= 0 || double.IsNaN(variance) || double.IsInfinity(variance))
            return 1.0;

        var sigma = Math.Sqrt(variance);

        // Continuity correction: shrink the gap by 0.5 (a discrete U approximated by a
        // continuous normal). Clamp so an exactly-central U yields p = 1.
        var corrected = Math.Max(0.0, Math.Abs(u - mu) - 0.5);
        var z = corrected / sigma;

        var pValue = 2.0 * (1.0 - NormalCdf(z));

        if (double.IsNaN(pValue))
            return 1.0;

        return Math.Clamp(pValue, 0.0, 1.0);
    }

    /// <summary>
    ///     Standard-normal CDF, <c>Φ(x) = ½·erfc(−x/√2)</c>, computed from an
    ///     erfc accurate to ~1e-15 relative. The previous Abramowitz-Stegun 7.1.26 form
    ///     had ~1.5e-7 absolute error - irrelevant at α = 0.05, but it made exported deep-tail
    ///     p-values (1e-9 and below) meaningless. This form keeps those tails honest.
    /// </summary>
    internal static double NormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2.0));

    /// <summary>
    ///     Complementary error function via W. J. Cody's rational Chebyshev approximation
    ///     (SPECFUN <c>CALERF</c>, 1969/1990), with |relative error| below ~1e-15 across the
    ///     whole range. Three sub-approximations cover |x| in [0, 0.46875), [0.46875, 4), and
    ///     [4, ∞).
    /// </summary>
    private static double Erfc(double x)
    {
        var y = Math.Abs(x);

        // Region 1: |x| < 0.46875 - approximate erf directly, then erfc = 1 - erf.
        if (y < 0.46875)
        {
            double[] a =
            [
                3.16112374387056560e00, 1.13864154151050156e02,
                3.77485237685302021e02, 3.20937758913846947e03, 1.85777706184603153e-1,
            ];
            double[] b =
            [
                2.36012909523441209e01, 2.44024637934444173e02,
                1.28261652607737228e03, 2.84423683343917062e03,
            ];

            var z = y * y;
            var num = a[4] * z;
            var den = z;

            for (var i = 0; i < 3; i++)
            {
                num = (num + a[i]) * z;
                den = (den + b[i]) * z;
            }

            var erf = x * (num + a[3]) / (den + b[3]);
            return 1.0 - erf;
        }

        double result;

        // Region 2: 0.46875 <= |x| <= 4.0.
        if (y <= 4.0)
        {
            double[] c =
            [
                5.64188496988670089e-1, 8.88314979438837594e00, 6.61191906371416295e01,
                2.98635138197400131e02, 8.81952221241769090e02, 1.71204761263407058e03,
                2.05107837782607147e03, 1.23033935479799725e03, 2.15311535474403846e-8,
            ];
            double[] d =
            [
                1.57449261107098347e01, 1.17693950891312499e02, 5.37181101862009858e02,
                1.62138957456669019e03, 3.29079923573345963e03, 4.36261909014324716e03,
                3.43936767414372164e03, 1.23033935480374942e03,
            ];

            var num = c[8] * y;
            var den = y;

            for (var i = 0; i < 7; i++)
            {
                num = (num + c[i]) * y;
                den = (den + d[i]) * y;
            }

            result = (num + c[7]) / (den + d[7]);
        }
        else
        {
            // Region 3: |x| > 4.0.
            double[] p =
            [
                3.05326634961232344e-1, 3.60344899949804439e-1, 1.25781726111229246e-1,
                1.60837851487422766e-2, 6.58749161529837803e-4, 1.63153871373020978e-2,
            ];
            double[] q =
            [
                2.56852019228982242e00, 1.87295284992346047e00, 5.27905102951428412e-1,
                6.05183413124413191e-2, 2.33520497626869185e-3,
            ];

            var z = 1.0 / (y * y);
            var num = p[5] * z;
            var den = z;

            for (var i = 0; i < 4; i++)
            {
                num = (num + p[i]) * z;
                den = (den + q[i]) * z;
            }

            result = z * (num + p[4]) / (den + q[4]);
            result = (0.5641895835477562869 - result) / y; // 1/sqrt(pi) - poly, scaled.
        }

        // Both regions 2 and 3 produce erfc(|x|) = exp(-x^2) * result. Reconstruct the
        // exponential factor with the standard split that preserves precision for large x.
        var xTrunc = Math.Floor(y * 16.0) / 16.0;
        var del = (y - xTrunc) * (y + xTrunc);
        result *= Math.Exp(-xTrunc * xTrunc) * Math.Exp(-del);

        // erfc is odd about erfc(-x) = 2 - erfc(x).
        return x < 0 ? 2.0 - result : result;
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
