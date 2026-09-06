using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests.Stats;

/// <summary>
///     The Winsorized (Yuen) standard error: three claims, tested separately.
///     <list type="number">
///         <item>
///             <description>
///                 It <b>reduces exactly</b>. With nothing trimmed it is <c>s / sqrt(n)</c> on
///                 <c>n - 1</c> degrees of freedom, so a clean run's reported numbers do not move.
///             </description>
///         </item>
///         <item>
///             <description>
///                 It <b>widens</b> where trimming happened, by clamping the trimmed samples rather
///                 than dropping them - so an extreme value counts as an observation without its
///                 magnitude setting the width.
///             </description>
///         </item>
///         <item>
///             <description>
///                 It matches the <b>reference definition</b> (Wilcox's Winsorized standard error,
///                 which R's <c>WRS2::trimse</c> implements) on values computed outside this
///                 codebase. Reference values are pinned in
///                 <see cref="WinsorizedErrorCrossCheckTests" />; this file covers behavior.
///             </description>
///         </item>
///     </list>
/// </summary>
public class WinsorizedErrorTests
{
    [Fact]
    public void Compute_WithNothingTrimmed_EqualsThePlainStandardError()
    {
        var samples = Sorted([102.3, 98.7, 110.1, 95.4, 103.8, 99.9, 101.2, 97.6, 105.5, 100.0]);

        var winsorized = WinsorizedError.Compute(new TrimContext(samples, 0, 0), 0.95);

        Assert.NotNull(winsorized);
        Numerics.AssertRelativeClose(NaiveStandardError(samples), winsorized.Value.StandardErrorNs, 1e-12);
        Numerics.AssertRelativeClose(NaiveStandardDeviation(samples), winsorized.Value.StandardDeviationNs, 1e-12);
        Assert.Equal(samples.Length - 1, winsorized.Value.DegreesOfFreedom);
    }

    /// <summary>
    ///     The worked example the design was checked against: <c>[1..9, 100]</c> with the top sample
    ///     trimmed. The 100 is clamped to 9 rather than dropped, so the interval widens by 10.7% over
    ///     the naive one - and not by the ~30x it would have if the outlier's magnitude counted.
    /// </summary>
    [Fact]
    public void Compute_ClampsTheTrimmedTail_RatherThanDroppingOrKeepingIt()
    {
        var samples = Sorted([1, 2, 3, 4, 5, 6, 7, 8, 9, 100]);
        var kept = Sorted([1, 2, 3, 4, 5, 6, 7, 8, 9]);

        var winsorized = WinsorizedError.Compute(new TrimContext(samples, 0, 1), 0.95);

        Assert.NotNull(winsorized);

        // Winsorized sample is [1..9, 9]: mean 5.4, sum of squared deviations 74.40.
        Numerics.AssertRelativeClose(2.8751811537130436, winsorized.Value.StandardDeviationNs, 1e-12);
        Numerics.AssertRelativeClose(1.0102356812582116, winsorized.Value.StandardErrorNs, 1e-12);
        Assert.Equal(8, winsorized.Value.DegreesOfFreedom);

        // Wider than the naive interval on the kept set, by a margin set by how many samples were
        // trimmed - not by how extreme they were.
        var naive = NaiveStandardError(kept);
        Assert.True(winsorized.Value.StandardErrorNs > naive);
        Numerics.AssertRelativeClose(1.107, winsorized.Value.StandardErrorNs / naive, 1e-3);
    }

    /// <summary>
    ///     The property that makes Winsorizing the right instrument rather than merely a wider one:
    ///     moving the trimmed outlier further out does not move the interval at all. A sample past
    ///     the fence is evidence that the run produced a reading out there, and the estimator counts
    ///     it as exactly that - one observation - never as its own magnitude.
    /// </summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(10_000.0)]
    [InlineData(1e9)]
    public void Compute_IsUnmovedBy_TheMagnitudeOfATrimmedSample(double outlier)
    {
        var samples = Sorted([1, 2, 3, 4, 5, 6, 7, 8, 9, outlier]);

        var winsorized = WinsorizedError.Compute(new TrimContext(samples, 0, 1), 0.95);

        Assert.NotNull(winsorized);
        Numerics.AssertRelativeClose(1.0102356812582116, winsorized.Value.StandardErrorNs, 1e-12);
    }

    [Fact]
    public void Compute_TrimmingBothTails_UsesTheRetainedEndpointsAsClamps()
    {
        // [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] with one trimmed off each end Winsorizes to
        // [2, 2, 3, 4, 5, 6, 7, 8, 9, 9]: mean 5.5, sum of squared deviations 66.5.
        var samples = Sorted([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        var winsorized = WinsorizedError.Compute(new TrimContext(samples, 1, 1), 0.95);

        Assert.NotNull(winsorized);
        Numerics.AssertRelativeClose(Math.Sqrt(66.5 / 9.0), winsorized.Value.StandardDeviationNs, 1e-12);
        Numerics.AssertRelativeClose(
            Math.Sqrt(66.5 / 9.0) * Math.Sqrt(10.0) / 8.0, winsorized.Value.StandardErrorNs, 1e-12);
        Assert.Equal(7, winsorized.Value.DegreesOfFreedom);
    }

    [Fact]
    public void Compute_MarginOfError_Is_TCritical_Times_TheStandardError()
    {
        var samples = Sorted([1, 2, 3, 4, 5, 6, 7, 8, 9, 100]);

        var winsorized = WinsorizedError.Compute(new TrimContext(samples, 0, 1), 0.99);

        Assert.NotNull(winsorized);

        // Read on h - 1 = 8 degrees of freedom, not n - 1 = 9: the interval is on the trimmed mean.
        var expected = StudentT.CriticalValue(0.99, 8) * winsorized.Value.StandardErrorNs;
        Numerics.AssertRelativeClose(expected, winsorized.Value.MarginOfErrorNs, 1e-12);
    }

    [Theory]
    [InlineData(1, 0, 0)] // n = 1: no variance to estimate.
    [InlineData(4, 2, 2)] // Everything trimmed: h = 0.
    [InlineData(4, 0, 3)] // h = 1: no degrees of freedom.
    public void Compute_ReturnsNull_WhenTheEstimatorIsUndefined(int n, int low, int high)
    {
        var samples = Sorted(Enumerable.Range(1, n).Select(i => (double)i).ToArray());

        Assert.Null(WinsorizedError.Compute(new TrimContext(samples, low, high), 0.95));
    }

    // ---- TrimContext.From ---------------------------------------------------

    [Fact]
    public void From_CountsTrimmedSamples_AtEachEnd()
    {
        var timings = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var trim = OutlierTrim.TrimDetailed(timings, OutlierMode.RemoveTopAndBottom5Percent);
        var context = TrimContext.From(trim);

        Assert.Equal(5, context.TrimmedLow);
        Assert.Equal(5, context.TrimmedHigh);
        Assert.True(context.IsTrimmed);
        Assert.Same(trim.SortedAll, context.SortedAll);
    }

    [Fact]
    public void From_ReportsNothingTrimmed_WhenTheDetectorKeptEverything()
    {
        var timings = Enumerable.Range(1, 40).Select(i => (double)i).ToArray();

        var context = TrimContext.From(OutlierTrim.TrimDetailed(timings, OutlierMode.None));

        Assert.Equal(0, context.TrimmedLow);
        Assert.Equal(0, context.TrimmedHigh);
        Assert.False(context.IsTrimmed);
    }

    /// <summary>
    ///     A one-sided fence - the shape almost every real benchmark produces, where a handful of
    ///     preempted samples sit past the upper fence and nothing sits past the lower one - has to
    ///     be counted as a high trim only. Counting it symmetrically would clamp fast samples that
    ///     were never trimmed.
    /// </summary>
    [Fact]
    public void From_CountsAOneSidedFence_OnTheSideItTrimmed()
    {
        var timings = Enumerable.Repeat(100.0, 40).Concat([5_000.0, 6_000.0]).ToArray();

        var context = TrimContext.From(OutlierTrim.TrimDetailed(timings, OutlierMode.IqrFence));

        Assert.Equal(0, context.TrimmedLow);
        Assert.Equal(2, context.TrimmedHigh);
    }

    private static double[] Sorted(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy;
    }

    private static double NaiveStandardDeviation(double[] values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
    }

    private static double NaiveStandardError(double[] values) =>
        NaiveStandardDeviation(values) / Math.Sqrt(values.Length);
}
