namespace NBenchmark.Stats;

/// <summary>
///     The result of a <see cref="KruskalWallis" /> omnibus test: the tie-corrected
///     <c>H</c> statistic, its degrees of freedom (<c>k − 1</c>), the resulting p-value, and
///     the number of groups compared.
/// </summary>
public readonly record struct KruskalWallisResult(double H, int DegreesOfFreedom, double PValue, int GroupCount)
{
    /// <summary>True when the test was computable (a numeric p-value was produced).</summary>
    public bool IsValid => !double.IsNaN(PValue);
}

/// <summary>
///     The Kruskal-Wallis H test: a non-parametric, rank-based omnibus test for whether
///     <c>k ≥ 2</c> independent groups are drawn from the same distribution. It is the
///     natural extension of the two-sample <see cref="MannWhitneyU" /> test to three or more
///     groups - using it for many groups avoids the inflated false-positive rate of running
///     a pairwise test on every pair.
///     <para>
///         The statistic is
///         <c>H = 12 / (N(N+1)) · Σ Rᵢ²/nᵢ − 3(N+1)</c>, computed on mid-ranks and divided by
///         the tie-correction factor <c>1 − Σ(t³ − t) / (N³ − N)</c>. Under the null
///         hypothesis <c>H</c> is approximately chi-squared with <c>k − 1</c> degrees of
///         freedom, which yields the p-value.
///     </para>
/// </summary>
public static class KruskalWallis
{
    /// <summary>Minimum number of groups the test is defined for.</summary>
    public const int MinGroups = 2;

    /// <summary>
    ///     Runs the omnibus test across <paramref name="groups" />. Returns a result whose
    ///     <see cref="KruskalWallisResult.PValue" /> is <see cref="double.NaN" /> when the
    ///     test is not defined (fewer than two groups, an empty group, or fewer than two
    ///     total observations).
    /// </summary>
    public static KruskalWallisResult Test(IReadOnlyList<double[]> groups)
    {
        var k = groups.Count;

        if (k < MinGroups)
            return new KruskalWallisResult(double.NaN, 0, double.NaN, k);

        var total = 0;

        foreach (var group in groups)
        {
            if (group.Length == 0)
                return new KruskalWallisResult(double.NaN, 0, double.NaN, k);

            total += group.Length;
        }

        if (total < 2)
            return new KruskalWallisResult(double.NaN, k - 1, double.NaN, k);

        var combined = new (double Value, int Group)[total];
        var index = 0;

        for (var g = 0; g < k; g++)
        {
            foreach (var value in groups[g])
            {
                combined[index++] = (value, g);
            }
        }

        Array.Sort(combined, (a, b) => a.Value.CompareTo(b.Value));

        var rankSums = new double[k];
        var tieCorrectionSum = 0.0;
        var i = 0;

        while (i < total)
        {
            var j = i + 1;

            while (j < total && combined[j].Value == combined[i].Value)
            {
                j++;
            }

            // Tied observations (positions i..j-1, 1-based ranks i+1..j) share the average rank.
            var meanRank = (i + j + 1) / 2.0;
            var tieSize = j - i;

            if (tieSize > 1)
                tieCorrectionSum += (double)tieSize * tieSize * tieSize - tieSize;

            for (var t = i; t < j; t++)
            {
                rankSums[combined[t].Group] += meanRank;
            }

            i = j;
        }

        var h = 0.0;

        for (var g = 0; g < k; g++)
        {
            h += rankSums[g] * rankSums[g] / groups[g].Length;
        }

        h = 12.0 / (total * (total + 1.0)) * h - 3.0 * (total + 1.0);

        // total >= 2 is enforced above, so N³ - N >= 6 and the denominator is never zero.
        var tieDenominator = (double)total * total * total - total;
        var correction = 1.0 - tieCorrectionSum / tieDenominator;

        // Correction == 0 means every observation is tied: there is no variation to detect.
        if (correction <= 0)
            return new KruskalWallisResult(0.0, k - 1, 1.0, k);

        h /= correction;

        var degreesOfFreedom = k - 1;
        var pValue = ChiSquared.SurvivalFunction(h, degreesOfFreedom);

        return new KruskalWallisResult(h, degreesOfFreedom, pValue, k);
    }
}
