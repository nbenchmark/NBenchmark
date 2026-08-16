using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests.Stats;

/// <summary>
///     External cross-check for the Winsorized (Yuen) standard error. The reference is not
///     <c>scipy.stats.trim_mean</c>, which returns a location estimate and no standard error at all;
///     it is Wilcox's Winsorized standard error, the quantity R's <c>WRS2::trimse</c> computes.
///     Reference values were produced by the independent generator recorded in
///     <c>docs/statistics/validation.md</c> and embedded below as constants.
/// </summary>
/// <remarks>
///     <para>
///         <c>WRS2::trimse(x, tr)</c> is <c>sqrt(winvar(x, tr)) / ((1 - 2·tr)·sqrt(n))</c>. For a
///         symmetric trim where <c>tr·n</c> is a whole number, <c>(1 - 2·tr)·sqrt(n)</c> is
///         <c>h / sqrt(n)</c>, so that expression and NBenchmark's <c>s_w · sqrt(n) / h</c> are the
///         same number written two ways - which is what
///         <see cref="Matches_The_WRS2_TrimSe_Formulation_On_A_Symmetric_Trim" /> pins. NBenchmark
///         takes trim <i>counts</i> rather than a proportion because a fence-based detector discards
///         however many samples fall past the fence, asymmetrically and without a proportion to name.
///     </para>
/// </remarks>
public class WinsorizedErrorCrossCheckTests
{
    private const double RelTol = 1e-9;

    /// <summary>Twenty timings with no extreme tail - the case a symmetric proportional trim is defined for.</summary>
    private static readonly double[] Timings20 =
    [
        102.3, 98.7, 110.1, 95.4, 103.8, 99.9, 101.2, 97.6, 105.5, 100.0,
        104.4, 96.8, 108.2, 94.1, 107.3, 93.5, 111.9, 99.1, 102.9, 100.7,
    ];

    /// <summary>The benchmarking shape: a tight body and one sample that lost the CPU.</summary>
    private static readonly double[] HeavyRightTail =
    [
        100.0, 101.0, 102.0, 103.0, 104.0, 105.0, 106.0, 107.0, 108.0, 109.0,
        110.0, 111.0, 112.0, 113.0, 114.0, 115.0, 116.0, 117.0, 118.0, 900.0,
    ];

    /// <summary>
    ///     Symmetric 10% trim on n = 20 (two samples off each end), the configuration where
    ///     <c>WRS2::trimse(x, tr = 0.1)</c> and NBenchmark's count-based form must agree exactly.
    /// </summary>
    [Fact]
    public void Matches_The_WRS2_TrimSe_Formulation_On_A_Symmetric_Trim()
    {
        var winsorized = Compute(Timings20, 2, 2);

        // Generator: winsorized standard deviation and standard error, n = 20, g_L = g_U = 2, h = 16.
        Numerics.AssertRelativeClose(4.404722346228922, winsorized.StandardDeviation, RelTol);
        Numerics.AssertRelativeClose(1.2311573235225293, winsorized.StandardError, RelTol);
        Assert.Equal(15, winsorized.DegreesOfFreedom);

        // The same value via WRS2's proportional expression, recomputed here from the Winsorized
        // standard deviation NBenchmark returned - so the identity is asserted, not assumed.
        const double tr = 0.1;
        var trimse = winsorized.StandardDeviation / ((1.0 - 2.0 * tr) * Math.Sqrt(Timings20.Length));
        Numerics.AssertRelativeClose(trimse, winsorized.StandardError, RelTol);
    }

    /// <summary>
    ///     Asymmetric trim - one sample off the slow end only, which is what an IQR fence produces on
    ///     a benchmark that was preempted once. There is no trim proportion to hand `WRS2::trimse`
    ///     here; the generator computes the Winsorized quantities directly from the definition.
    /// </summary>
    [Fact]
    public void Matches_The_Reference_On_An_Asymmetric_OneSided_Trim()
    {
        var winsorized = Compute(HeavyRightTail, 0, 1);

        // Generator: n = 20, g_L = 0, g_U = 1, h = 19. Winsorized mean 109.45, Σ(w - w̄)² = 646.95.
        Numerics.AssertRelativeClose(5.835237784358064, winsorized.StandardDeviation, RelTol);
        Numerics.AssertRelativeClose(1.3734724579684094, winsorized.StandardError, RelTol);
        Assert.Equal(18, winsorized.DegreesOfFreedom);
    }

    /// <summary>
    ///     The two numbers the Winsorized estimator sits between, on the same data. Dropping the
    ///     outlier reports ±1.29 on a body whose raw sample set has a standard error of ±39.6; the
    ///     Winsorized answer is 1.37, wider than the trimmed one by the samples that were removed and
    ///     nowhere near the raw one, because the outlier's magnitude is deliberately not in it. This
    ///     is the "what it does and does not fix" claim, as an assertion.
    /// </summary>
    [Fact]
    public void Sits_Above_The_Trimmed_Standard_Error_And_Far_Below_The_Raw_One()
    {
        var winsorized = Compute(HeavyRightTail, 0, 1);

        // Generator: naive s/sqrt(n) on the kept 19 samples, and on all 20.
        const double trimmedStandardError = 1.2909944487358054;
        const double rawStandardError = 39.5689587934785;

        Assert.True(winsorized.StandardError > trimmedStandardError);
        Assert.True(winsorized.StandardError < rawStandardError);
        Numerics.AssertRelativeClose(1.0639, winsorized.StandardError / trimmedStandardError, 1e-4);
    }

    private static WinsorizedSpread Compute(double[] values, int trimmedLow, int trimmedHigh)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var winsorized = WinsorizedError.Compute(new TrimContext(sorted, trimmedLow, trimmedHigh), 0.95);

        Assert.NotNull(winsorized);
        return winsorized.Value;
    }
}
