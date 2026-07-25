using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class MeasurementGatesTests
{
    // ---------- TimeFloorMet ----------

    [Fact]
    public void TimeFloor_Zero_Is_Always_Met()
    {
        // A zero floor means the user asked for MinSamples alone.
        Assert.True(MeasurementGates.TimeFloorMet(count: 1, measurementNs: 0, minMeasurementTimeNs: 0, sampleCeiling: 500));
    }

    [Theory]
    [InlineData(99_999_999.0, false)] // just short of the floor
    [InlineData(100_000_000.0, true)] // exactly at the floor (>=)
    [InlineData(100_000_001.0, true)]
    public void TimeFloor_Boundary_Is_Inclusive(double measurementNs, bool expected)
    {
        Assert.Equal(
            expected,
            MeasurementGates.TimeFloorMet(count: 10, measurementNs, minMeasurementTimeNs: 100_000_000.0, sampleCeiling: 500));
    }

    [Theory]
    [InlineData(4_999, false)]
    [InlineData(5_000, true)]
    [InlineData(5_001, true)]
    public void TimeFloor_Is_Satisfied_By_The_Sample_Ceiling(int count, bool expected)
    {
        // A nano-scale body can never accumulate the duration, so the ceiling has to release it.
        Assert.Equal(
            expected,
            MeasurementGates.TimeFloorMet(count, measurementNs: 1_000.0, minMeasurementTimeNs: 100_000_000.0, sampleCeiling: 5_000));
    }

    [Theory]
    [InlineData(30, 5_000, 5_000)] // default preset
    [InlineData(15, 2_000, 2_000)] // quick preset
    [InlineData(100, 20_000, 20_000)] // thorough preset
    // A MaxSamples below MinSamples resolves to MinSamples, mirroring CiWidthDetector's own
    // Max(_minSamples, MaxSamples) clamp - so the floor never exceeds the ceiling the detector uses.
    [InlineData(30, 5, 30)]
    public void TimeFloorCeiling_Is_The_Effective_Sample_Ceiling(int minSamples, int maxSamples, int expected)
    {
        Assert.Equal(expected, MeasurementGates.ResolveTimeFloorCeiling(minSamples, maxSamples));
    }

    [Fact]
    public void TimeFloorCeiling_Does_Not_Cut_The_Duration_Floor_Short_For_A_Mid_Speed_Body()
    {
        // Regression guard for a real defect found in verification. An earlier design capped the floor
        // at a tenth of MaxSamples, which silently defeated it across a wide middle band: a 40 us
        // sample under Quick needs 1,250 samples to reach the 50 ms floor but was released at 200, and
        // a body still mid-tier-up there stopped on the CI target reporting a number ~4x off its
        // steady state. MaxSamples already bounds the loop, so no earlier cap is needed.
        var ceiling = MeasurementGates.ResolveTimeFloorCeiling(minSamples: 15, maxSamples: 2_000);

        const double fortyMicrosecondsNs = 40_000.0;
        const double fiftyMillisecondsNs = 50_000_000.0;

        // At 200 samples the duration is nowhere near met, and the ceiling must not release it.
        Assert.False(MeasurementGates.TimeFloorMet(200, 200 * fortyMicrosecondsNs, fiftyMillisecondsNs, ceiling));

        // It is released at 1,250 samples, by the duration, exactly as designed.
        Assert.True(MeasurementGates.TimeFloorMet(1_250, 1_250 * fortyMicrosecondsNs, fiftyMillisecondsNs, ceiling));
    }

    // ---------- IsSteady ----------

    [Fact]
    public void IsSteady_Blocks_A_Real_Step_Change()
    {
        // The SortLargeArray signature from the reproducing data: first-half ~4.80 ms, second-half
        // ~2.22 ms (a 2.16x drop as tier-1 landed mid-measurement), with tight within-regime spread.
        Assert.False(MeasurementGates.IsSteady(
            firstHalfMean: 4_800_000, secondHalfMean: 2_220_000, count: 124,
            standardDeviation: 600_000, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Blocks_A_Step_Change_In_Either_Direction()
    {
        // The relative arm divides by the smaller half-mean, so a body that got slower trips at the
        // same magnitude as one that got faster.
        Assert.False(MeasurementGates.IsSteady(
            firstHalfMean: 2_220_000, secondHalfMean: 4_800_000, count: 124,
            standardDeviation: 600_000, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Allows_Sub_Percent_Drift_At_Large_N()
    {
        // 0.5% apart at n = 5,000 clears any p-value threshold but is far too small to act on. A bare
        // significance rule would flag this; the relative arm lets it through.
        Assert.True(MeasurementGates.IsSteady(
            firstHalfMean: 1_000.0, secondHalfMean: 1_005.0, count: 5_000,
            standardDeviation: 10.0, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Allows_A_Large_Gap_That_Is_Only_Noise()
    {
        // MemoryCopy run 1 from the reproducing data: CV 581% at a ~2 us mean. Half-means routinely sit
        // far more than 10% apart from pure sampling noise, so the relative arm alone would flag this
        // benchmark on every single run and it would never converge. The sigma arm is what saves it.
        const double mean = 2_000.0;
        const double sd = mean * 5.81;

        // Standard error of the difference at n = 200 is sd * 2/sqrt(200) ~= 1,644, so a 4-sigma
        // allowance is ~6,576 - comfortably above this 700 ns gap.
        Assert.True(MeasurementGates.IsSteady(
            firstHalfMean: mean, secondHalfMean: mean + 700, count: 200,
            standardDeviation: sd, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Blocks_When_A_Large_Gap_Also_Clears_The_Sigma_Bar()
    {
        // Same heavy tail, but now the gap is large enough to be real rather than noise.
        const double mean = 2_000.0;
        const double sd = mean * 5.81;

        Assert.False(MeasurementGates.IsSteady(
            firstHalfMean: mean, secondHalfMean: mean * 8, count: 200,
            standardDeviation: sd, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Zero_Tolerance_Disables_The_Gate()
    {
        Assert.True(MeasurementGates.IsSteady(
            firstHalfMean: 100, secondHalfMean: 10_000, count: 100,
            standardDeviation: 1.0, relativeTolerance: 0.0, sigmaTolerance: 4.0));
    }

    [Theory]
    [InlineData(0.0, 100.0)]
    [InlineData(100.0, 0.0)]
    [InlineData(-5.0, 100.0)]
    [InlineData(double.NaN, 100.0)]
    [InlineData(100.0, double.PositiveInfinity)]
    public void IsSteady_Fails_Open_On_Degenerate_Means(double first, double second)
    {
        // A degenerate mean carries no signal. It also already leaves the CI detector's relative
        // half-width infinite, so it can never stop on the CI target anyway.
        Assert.True(MeasurementGates.IsSteady(
            first, second, count: 100, standardDeviation: 1.0, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Allows_Fewer_Than_Two_Samples()
    {
        Assert.True(MeasurementGates.IsSteady(
            firstHalfMean: 0, secondHalfMean: 0, count: 1,
            standardDeviation: 0, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    [Fact]
    public void IsSteady_Blocks_A_Large_Gap_With_Zero_Variance()
    {
        // Zero spread means the sigma arm has no allowance to give: the gap is exact, so it is real.
        Assert.False(MeasurementGates.IsSteady(
            firstHalfMean: 7_000, secondHalfMean: 68_000, count: 16,
            standardDeviation: 0.0, relativeTolerance: 0.10, sigmaTolerance: 4.0));
    }

    // ---------- SplitHalfDrift ----------

    [Fact]
    public void SplitHalfDrift_Is_Relative_To_The_Smaller_Half()
    {
        Assert.Equal(1.0, MeasurementGates.SplitHalfDrift(100, 200), 12);
        Assert.Equal(1.0, MeasurementGates.SplitHalfDrift(200, 100), 12);
        Assert.Equal(0.0, MeasurementGates.SplitHalfDrift(100, 100), 12);
    }

    [Theory]
    [InlineData(0.0, 100.0)]
    [InlineData(100.0, double.NaN)]
    public void SplitHalfDrift_Is_Zero_For_Degenerate_Means(double first, double second)
    {
        Assert.Equal(0.0, MeasurementGates.SplitHalfDrift(first, second));
    }
}

public class SplitHalfTrackerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(101)]
    public void Incremental_Half_Means_Match_A_Naive_Recomputation(int n)
    {
        // The incremental form is the whole point (O(1) per sample on the measurement hot path), so it
        // has to agree exactly with the obvious implementation - at odd and even n alike.
        var samples = new List<double>();
        var tracker = new SplitHalfTracker(samples);

        for (var i = 1; i <= n; i++)
        {
            var value = 100.0 + i;
            samples.Add(value);
            tracker.Add(value);
        }

        var half = n / 2;
        var expectedFirst = half > 0 ? samples.Take(half).Average() : 0.0;
        var expectedSecond = n - half > 0 ? samples.Skip(half).Average() : 0.0;

        Assert.Equal(n, tracker.Count);
        Assert.Equal(expectedFirst, tracker.FirstHalfMean, 9);
        Assert.Equal(expectedSecond, tracker.SecondHalfMean, 9);
    }

    [Fact]
    public void Reset_Clears_All_State()
    {
        var samples = new List<double>();
        var tracker = new SplitHalfTracker(samples);

        for (var i = 0; i < 10; i++)
        {
            samples.Add(i);
            tracker.Add(i);
        }

        Assert.Equal(10, tracker.Count);

        // The caller clears the backing list and the tracker together on a drift restart.
        samples.Clear();
        tracker.Reset();

        Assert.Equal(0, tracker.Count);
        Assert.Equal(0.0, tracker.FirstHalfMean);
        Assert.Equal(0.0, tracker.SecondHalfMean);

        // And it must be usable again afterwards.
        for (var i = 0; i < 4; i++)
        {
            samples.Add(50.0);
            tracker.Add(50.0);
        }

        Assert.Equal(4, tracker.Count);
        Assert.Equal(50.0, tracker.FirstHalfMean, 9);
        Assert.Equal(50.0, tracker.SecondHalfMean, 9);
    }

    [Fact]
    public void Detects_A_Step_Change_Between_The_Halves()
    {
        var samples = new List<double>();
        var tracker = new SplitHalfTracker(samples);

        // 40 slow samples then 60 fast ones - the shape of a tier-up landing inside measurement.
        for (var i = 0; i < 100; i++)
        {
            var value = i < 40 ? 4_800_000.0 : 2_220_000.0;
            samples.Add(value);
            tracker.Add(value);
        }

        Assert.True(tracker.FirstHalfMean > tracker.SecondHalfMean);
        Assert.Equal(2_220_000.0, tracker.SecondHalfMean, 6);
    }
}
