using Xunit;

namespace NBenchmark.Tests;

public class MeasurementOptionsTests
{
    [Fact]
    public void Default_Has_Expected_Values()
    {
        var opts = MeasurementOptions.Default;

        Assert.Null(opts.WarmupIterations);
        Assert.Null(opts.Iterations);
        Assert.Null(opts.OpsPerSample);
        Assert.Equal(AutoTuneOptions.Default, opts.AutoTune);
        Assert.Equal(MeasurementProfile.Realistic, opts.Profile);
        Assert.False(opts.ForceGcBeforeEachIteration);
        Assert.False(opts.ForceGcBetweenBenchmarks);
        Assert.True(opts.MeasureAllocations);
        Assert.Equal(OutlierMode.IqrFence, opts.OutlierMode);
        Assert.Equal(0.95, opts.ConfidenceLevel);
        Assert.Equal([0.50, 0.95, 0.99, 0.999, 1.0], opts.ReportedPercentiles);
        Assert.True(opts.EnableHistogram);
        Assert.Equal(20, opts.HistogramBucketCount);
        Assert.True(opts.EnableSignificance);
        Assert.Equal(0.05, opts.SignificanceLevel);
        Assert.Null(opts.MinimumPracticalEffect);
        Assert.Equal(1, opts.LaunchCount);
        Assert.Equal(DiagnosticsOptions.Default, opts.Diagnostics);
    }

    [Theory]
    [InlineData(DiagnosticsMode.None)]
    [InlineData(DiagnosticsMode.Gc)]
    [InlineData(DiagnosticsMode.GcAndCpu)]
    [InlineData(DiagnosticsMode.All)]
    [InlineData(DiagnosticsMode.Exceptions)]
    [InlineData(DiagnosticsMode.GcHeapInfo | DiagnosticsMode.Exceptions)]
    public void DiagnosticsOptions_FromMode_ToMode_RoundTrips(DiagnosticsMode mode)
    {
        var options = DiagnosticsOptions.FromMode(mode);

        Assert.Equal(mode, options.ToMode());
    }

    [Fact]
    public void ReportedPercentiles_Are_Normalized_To_Sorted_Distinct()
    {
        var opts = new MeasurementOptions
        {
            ReportedPercentiles = [0.99, 0.95, 0.95, 1.0, 0.50],
        };

        Assert.Equal([0.50, 0.95, 0.99, 1.0], opts.ReportedPercentiles);
    }

    [Fact]
    public void ReportedPercentiles_Rejects_Invalid_Values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { ReportedPercentiles = [-0.01, 0.95] });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { ReportedPercentiles = [1.1] });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { ReportedPercentiles = [double.NaN] });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { ReportedPercentiles = [double.PositiveInfinity] });
    }

    [Theory]
    [InlineData(4)]
    [InlineData(101)]
    public void HistogramBucketCount_Rejects_Invalid_Values(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { HistogramBucketCount = value });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(100)]
    public void HistogramBucketCount_Accepts_Valid_Values(int value)
    {
        var opts = new MeasurementOptions { HistogramBucketCount = value };
        Assert.Equal(value, opts.HistogramBucketCount);
    }

    [Fact]
    public void Independent_ForcesGcAndDisablesAlloc()
    {
        var opts = MeasurementOptions.For(MeasurementProfile.Independent);

        Assert.Equal(MeasurementProfile.Independent, opts.Profile);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.True(opts.ForceGcBetweenBenchmarks);
        Assert.False(opts.MeasureAllocations);
    }

    [Fact]
    public void WithProfile_ResolvesOptionBundle()
    {
        var opts = new MeasurementOptions() with { Profile = MeasurementProfile.Independent };

        Assert.Equal(MeasurementProfile.Independent, opts.Profile);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.True(opts.ForceGcBetweenBenchmarks);
        Assert.False(opts.MeasureAllocations);
    }

    [Fact]
    public void ExplicitOverrideWinsOverProfile()
    {
        var opts = new MeasurementOptions() with
        {
            Profile = MeasurementProfile.Realistic,
            ForceGcBeforeEachIterationOverride = true,
        };

        Assert.Equal(MeasurementProfile.Realistic, opts.Profile);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.False(opts.ForceGcBetweenBenchmarks);
        Assert.True(opts.MeasureAllocations);
    }

    [Fact]
    public void OverrideSurvivesWithProfileChange()
    {
        var opts = MeasurementOptions.For(MeasurementProfile.Independent) with
        {
            Profile = MeasurementProfile.Realistic,
            ForceGcBeforeEachIterationOverride = true,
        };

        Assert.Equal(MeasurementProfile.Realistic, opts.Profile);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.False(opts.ForceGcBetweenBenchmarks);
        Assert.True(opts.MeasureAllocations);
    }

    [Fact]
    public void NoAllocationsOverrideDisablesUnderRealistic()
    {
        var opts = new MeasurementOptions() with
        {
            Profile = MeasurementProfile.Realistic,
            MeasureAllocationsOverride = false,
        };

        Assert.False(opts.MeasureAllocations);
    }

    [Fact]
    public void ForceGcOverrideEnablesUnderRealistic()
    {
        var opts = new MeasurementOptions() with
        {
            Profile = MeasurementProfile.Realistic,
            ForceGcBeforeEachIterationOverride = true,
        };

        Assert.True(opts.ForceGcBeforeEachIteration);
    }

    [Fact]
    public void ForceGcBetweenBenchmarksOverrideEnablesUnderRealistic()
    {
        var opts = new MeasurementOptions() with
        {
            Profile = MeasurementProfile.Realistic,
            ForceGcBetweenBenchmarksOverride = true,
        };

        Assert.True(opts.ForceGcBetweenBenchmarks);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.147)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void MinimumPracticalEffect_Accepts_Valid_Values(double value)
    {
        var opts = new MeasurementOptions { MinimumPracticalEffect = value };
        Assert.Equal(value, opts.MinimumPracticalEffect);
    }

    [Fact]
    public void MinimumPracticalEffect_Rejects_Invalid_Values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { MinimumPracticalEffect = -0.01 });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { MinimumPracticalEffect = 1.01 });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { MinimumPracticalEffect = double.NaN });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { MinimumPracticalEffect = double.PositiveInfinity });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { MinimumPracticalEffect = double.NegativeInfinity });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void Iterations_Rejects_Invalid_Values(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { Iterations = value });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(100000)]
    public void Iterations_Accepts_Valid_Values(int value)
    {
        var opts = new MeasurementOptions { Iterations = value };
        Assert.Equal(value, opts.Iterations);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void WarmupIterations_Rejects_Invalid_Values(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { WarmupIterations = value });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(10000)]
    public void WarmupIterations_Accepts_Valid_Values(int value)
    {
        var opts = new MeasurementOptions { WarmupIterations = value };
        Assert.Equal(value, opts.WarmupIterations);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void ConfidenceLevel_Rejects_Invalid_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { ConfidenceLevel = value });
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void ConfidenceLevel_Accepts_Valid_Values(double value)
    {
        var opts = new MeasurementOptions { ConfidenceLevel = value };
        Assert.Equal(value, opts.ConfidenceLevel);
    }

    // ---------- AutoTuneOptions grace-ceiling and budget-share validation ----------

    [Fact]
    public void AutoTune_Default_WarmupBudgetFraction_Is_0_4()
    {
        Assert.Equal(0.4, AutoTuneOptions.Default.WarmupBudgetFraction);
    }

    [Fact]
    public void AutoTune_Default_CapGraceFactor_Is_1_5()
    {
        Assert.Equal(1.5, AutoTuneOptions.Default.CapGraceFactor);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void WarmupBudgetFraction_Rejects_Invalid_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AutoTuneOptions { WarmupBudgetFraction = value });
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.4)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void WarmupBudgetFraction_Accepts_Valid_Values(double value)
    {
        var opts = new AutoTuneOptions { WarmupBudgetFraction = value };
        Assert.Equal(value, opts.WarmupBudgetFraction);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void CapGraceFactor_Rejects_Invalid_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AutoTuneOptions { CapGraceFactor = value });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(10.0)]
    public void CapGraceFactor_Accepts_Valid_Values(double value)
    {
        var opts = new AutoTuneOptions { CapGraceFactor = value };
        Assert.Equal(value, opts.CapGraceFactor);
    }

    // ---------- Target sample duration, warmup time floor, JIT-quiescence gate, and presets ----------

    [Fact]
    public void AutoTune_Default_TargetSampleDurationNs_Is_10us()
    {
        Assert.Equal(10_000, AutoTuneOptions.Default.TargetSampleDurationNs);
    }

    [Fact]
    public void AutoTune_Default_MinWarmupTime_Is_100ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), AutoTuneOptions.Default.MinWarmupTime);
    }

    [Fact]
    public void AutoTune_Default_RequireJitQuiescence_Is_True()
    {
        Assert.True(AutoTuneOptions.Default.RequireJitQuiescence);
    }

    [Fact]
    public void Quick_Preset_Tunes_Warmup_For_Fast_Feedback()
    {
        var quick = AutoTuneOptions.Quick;

        Assert.Equal(4, quick.BatchSize);
        Assert.Equal(2, quick.PlateauPatience);
        Assert.Equal(TimeSpan.FromMilliseconds(25), quick.MinWarmupTime);
    }

    [Fact]
    public void Thorough_Preset_Uses_50us_Target()
    {
        Assert.Equal(50_000, AutoTuneOptions.Thorough.TargetSampleDurationNs);
    }

    [Fact]
    public void MinWarmupTime_Accepts_Zero()
    {
        var opts = new AutoTuneOptions { MinWarmupTime = TimeSpan.Zero };
        Assert.Equal(TimeSpan.Zero, opts.MinWarmupTime);
    }

    [Fact]
    public void MinWarmupTime_Rejects_Negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AutoTuneOptions { MinWarmupTime = TimeSpan.FromMilliseconds(-1) });
    }

    [Fact]
    public void RequireJitQuiescence_Can_Be_Disabled()
    {
        var opts = new AutoTuneOptions { RequireJitQuiescence = false };
        Assert.False(opts.RequireJitQuiescence);
    }
}
