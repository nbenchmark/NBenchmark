using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Cross-checks for the Mann-Whitney U test against SciPy 1.17.1, plus a
///     self-contained exact-permutation enumerator that validates how far the
///     normal approximation NBenchmark uses can stray from the exact p-value on
///     small samples.
///     NBenchmark uses the large-sample normal approximation *without* a
///     continuity correction, which corresponds to
///     <c>scipy.stats.mannwhitneyu(..., method='asymptotic', use_continuity=False)</c>.
/// </summary>
public class MannWhitneyCrossCheckTests
{
    // Each case: two equal-length, tie-free samples; the SciPy asymptotic
    // (no continuity correction) two-sided p-value; and the SciPy *exact* p-value.
    public static IEnumerable<object[]> Cases()
    {
        yield return
        [
            new double[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            new double[] { 9, 10, 11, 12, 13, 14, 15, 16 },
            0.0007775304469403847, // asymptotic, no continuity correction
            0.0001554001554001554, // exact
        ];

        yield return
        [
            new double[] { 1, 3, 5, 7, 9, 11, 13, 15 },
            new double[] { 2, 4, 6, 8, 10, 12, 14, 16 },
            0.6744240722352938,
            0.7209013209013208,
        ];

        yield return
        [
            new double[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 },
            new double[] { 1, 2, 3, 4, 15, 16, 17, 18, 19, 20 },
            0.4496917979688909,
            0.48125094719521977,
        ];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Test_Matches_Scipy_Asymptotic(double[] a, double[] b, double asymptoticP, double exactP)
    {
        _ = exactP;
        var p = MannWhitneyU.Test(a, b);

        // Absolute tolerance: the only gap is the rational erf approximation
        // (|error| < 2e-7), which dominates relative error for tiny p-values.
        Assert.True(
            Math.Abs(p - asymptoticP) < 1e-6,
            $"|{p} − {asymptoticP}| = {Math.Abs(p - asymptoticP)} ≥ 1e-6.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Exact_Enumerator_Matches_Scipy(double[] a, double[] b, double asymptoticP, double exactP)
    {
        _ = asymptoticP;

        // Validates the in-test enumerator below against SciPy's exact method.
        var exact = ExactTwoSidedPValue(a, b);
        Numerics.AssertRelativeClose(exactP, exact, 1e-9);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Asymptotic_Approximation_Within_Documented_Bound_Of_Exact(
        double[] a, double[] b, double asymptoticP, double exactP)
    {
        _ = (asymptoticP, exactP);

        var approx = MannWhitneyU.Test(a, b);
        var exact = ExactTwoSidedPValue(a, b);

        // For n ≈ 8–10 per group the normal approximation can differ from the
        // exact permutation p-value by up to ≈ 0.05. This pins that the gap stays
        // within the documented bound and does not silently widen.
        Assert.True(
            Math.Abs(approx - exact) < 0.06,
            $"|approx {approx} − exact {exact}| = {Math.Abs(approx - exact)} ≥ 0.06.");
    }

    // Exact two-sided Mann-Whitney p-value by full enumeration of which combined
    // ranks belong to group A (assumes no ties). Matches SciPy's exact method.
    private static double ExactTwoSidedPValue(double[] a, double[] b)
    {
        var n1 = a.Length;
        var n2 = b.Length;
        var total = n1 + n2;

        var combined = new (double Value, int Group)[total];

        for (var i = 0; i < n1; i++)
        {
            combined[i] = (a[i], 0);
        }

        for (var i = 0; i < n2; i++)
        {
            combined[n1 + i] = (b[i], 1);
        }

        Array.Sort(combined, (x, y) => x.Value.CompareTo(y.Value));

        double rankSumA = 0;

        for (var i = 0; i < total; i++)
        {
            if (combined[i].Group == 0)
                rankSumA += i + 1;
        }

        var u1Observed = rankSumA - (double)n1 * (n1 + 1) / 2.0;
        var mu = (double)n1 * n2 / 2.0;
        var observedDistance = Math.Abs(u1Observed - mu);

        long extreme = 0;
        long count = 0;
        var combo = new int[n1];

        foreach (var rankSum in EnumerateCombinationSums(total, n1, combo))
        {
            var u1 = rankSum - (double)n1 * (n1 + 1) / 2.0;

            if (Math.Abs(u1 - mu) >= observedDistance - 1e-9)
                extreme++;

            count++;
        }

        return Math.Min(1.0, (double)extreme / count);
    }

    // Enumerates every n1-subset of the ranks {1..N}, yielding each subset's rank sum.
    private static IEnumerable<double> EnumerateCombinationSums(int n, int k, int[] combo)
    {
        for (var i = 0; i < k; i++)
        {
            combo[i] = i + 1;
        }

        while (true)
        {
            double sum = 0;

            for (var i = 0; i < k; i++)
            {
                sum += combo[i];
            }

            yield return sum;

            var pos = k - 1;

            while (pos >= 0 && combo[pos] == n - k + pos + 1)
            {
                pos--;
            }

            if (pos < 0)
                yield break;

            combo[pos]++;

            for (var i = pos + 1; i < k; i++)
            {
                combo[i] = combo[i - 1] + 1;
            }
        }
    }
}
