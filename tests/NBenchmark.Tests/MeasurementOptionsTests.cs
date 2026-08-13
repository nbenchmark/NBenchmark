using NBenchmark.Workers;
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
        Assert.False(opts.ForceGcBeforeMeasurement);
        Assert.True(opts.ForceGcBetweenBenchmarks);
        Assert.True(opts.MeasureAllocations);
        Assert.Equal(OutlierMode.IqrFence, opts.OutlierMode);
        Assert.Equal(0.95, opts.ConfidenceLevel);
        Assert.Equal([0.50, 0.95, 0.99, 0.999, 1.0], opts.ReportedPercentiles);
        Assert.True(opts.EnableHistogram);
        Assert.Equal(20, opts.HistogramBucketCount);
        Assert.True(opts.EnableSignificance);
        Assert.Equal(0.05, opts.SignificanceLevel);
        Assert.Equal(0.147, opts.MinimumPracticalEffect);
        Assert.Equal(MeasurementOptions.DefaultMinimumPracticalEffect, opts.MinimumPracticalEffect);
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

    /// <summary>
    ///     The transferred-state budget is bounded, because raising it past the transport's own limit
    ///     exchanges a refusal that names the remedy for a frame that cannot be written at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MeasurementOptions.MaxTransferredStateCeiling + 1)]
    [InlineData(int.MaxValue)]
    public void MaxTransferredStateBytes_Rejects_Out_Of_Range_Values(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MeasurementOptions { MaxTransferredStateBytes = value });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(MeasurementOptions.DefaultMaxTransferredStateBytes)]
    [InlineData(MeasurementOptions.MaxTransferredStateCeiling)]
    public void MaxTransferredStateBytes_Accepts_Valid_Values(int value)
    {
        var opts = new MeasurementOptions { MaxTransferredStateBytes = value };

        Assert.Equal(value, opts.MaxTransferredStateBytes);
    }

    [Fact]
    public void MaxTransferredStateCeiling_Stays_Under_The_Frame_Ceiling()
        => Assert.True(MeasurementOptions.MaxTransferredStateCeiling < WorkerProtocol.MaxFrameBytes);

    [Fact]
    public void Independent_ForcesGc_And_KeepsAllocationTracking()
    {
        var opts = MeasurementOptions.For(MeasurementProfile.Independent);

        Assert.Equal(MeasurementProfile.Independent, opts.Profile);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.True(opts.ForceGcBeforeMeasurement);
        Assert.True(opts.ForceGcBetweenBenchmarks);
        // Allocation tracking is on for both profiles now - it is measured outside the timed
        // window, so it costs nothing and surfaces the "this pure-CPU body allocates" signal.
        Assert.True(opts.MeasureAllocations);
    }

    [Fact]
    public void Realistic_InheritsWarmupHeap_But_StillGcsBetweenBenchmarks()
    {
        var opts = MeasurementOptions.For(MeasurementProfile.Realistic);

        // The pre-measurement GC is off (Realistic inherits the warmup heap to match production),
        // but the between-benchmark GC is on so one benchmark cannot bias the next.
        Assert.False(opts.ForceGcBeforeMeasurement);
        Assert.True(opts.ForceGcBetweenBenchmarks);
    }

    [Fact]
    public void WithProfile_ResolvesOptionBundle()
    {
        var opts = new MeasurementOptions() with { Profile = MeasurementProfile.Independent };

        Assert.Equal(MeasurementProfile.Independent, opts.Profile);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.True(opts.ForceGcBeforeMeasurement);
        Assert.True(opts.ForceGcBetweenBenchmarks);
        Assert.True(opts.MeasureAllocations);
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
        Assert.False(opts.ForceGcBeforeMeasurement);
        Assert.True(opts.ForceGcBetweenBenchmarks);
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
        Assert.False(opts.ForceGcBeforeMeasurement);
        Assert.True(opts.ForceGcBetweenBenchmarks);
        Assert.True(opts.MeasureAllocations);
    }

    [Fact]
    public void ForceGcBeforeMeasurementOverride_WinsOverProfile()
    {
        var independent = new MeasurementOptions
        {
            Profile = MeasurementProfile.Independent,
            ForceGcBeforeMeasurementOverride = false,
        };
        Assert.False(independent.ForceGcBeforeMeasurement);

        var realistic = new MeasurementOptions
        {
            Profile = MeasurementProfile.Realistic,
            ForceGcBeforeMeasurementOverride = true,
        };
        Assert.True(realistic.ForceGcBeforeMeasurement);
    }

    [Fact]
    public void ForceGcBetweenBenchmarksOverride_DisablesForBothProfiles()
    {
        var realistic = new MeasurementOptions
        {
            Profile = MeasurementProfile.Realistic,
            ForceGcBetweenBenchmarksOverride = false,
        };
        Assert.False(realistic.ForceGcBetweenBenchmarks);

        var independent = new MeasurementOptions
        {
            Profile = MeasurementProfile.Independent,
            ForceGcBetweenBenchmarksOverride = false,
        };
        Assert.False(independent.ForceGcBetweenBenchmarks);
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
    public void AutoTune_Default_MinWarmupTime_Is_500ms()
    {
        // 5x the runtime's 100 ms tiered-compilation call-counting delay, and chosen empirically: at
        // 250 ms a StringBuilder-append loop landed in either its tier-0 or its ~4.5x-faster steady
        // state depending on the run (a 4.8x run-to-run median spread); 500 ms made it consistent.
        Assert.Equal(TimeSpan.FromMilliseconds(500), AutoTuneOptions.Default.MinWarmupTime);
    }

    [Fact]
    public void AutoTune_Default_RequireJitQuiescence_Is_True()
    {
        Assert.True(AutoTuneOptions.Default.RequireJitQuiescence);
    }

    [Fact]
    public void AutoTune_Default_MaxWarmup_Far_Exceeds_The_Pinned_Limit()
    {
        // A count ceiling that binds before MinWarmupTime silently defeats the floor: a fast body needs
        // ~25,000 samples at the 10 us sample target to accumulate 250 ms.
        Assert.Equal(MeasurementOptions.MaxAutoWarmupIterations, AutoTuneOptions.Default.MaxWarmup);
        Assert.Equal(100_000, AutoTuneOptions.Default.MaxWarmup);
        Assert.True(AutoTuneOptions.Default.MaxWarmup > MeasurementOptions.MaxWarmupIterations);
    }

    [Fact]
    public void AutoTune_Default_Steady_State_Knobs()
    {
        var d = AutoTuneOptions.Default;

        Assert.Equal(TimeSpan.FromMilliseconds(50), d.JitQuietPeriod);
        Assert.Equal(TimeSpan.FromMilliseconds(100), d.MinMeasurementTime);
        Assert.Equal(0.10, d.MeasurementDriftTolerance);
        Assert.Equal(2, d.MeasurementRestartLimit);
    }

    [Fact]
    public void AutoTune_Default_MaxSamples_Is_5000()
    {
        // 100,000 was inherited from MeasurementOptions.MaxIterations, not chosen on measurement
        // grounds; at that ceiling a body with a CV in the hundreds of percent burns tens of thousands
        // of samples chasing a target more samples cannot reach.
        Assert.Equal(5_000, AutoTuneOptions.Default.MaxSamples);
    }

    [Fact]
    public void Quick_Preset_Tunes_Warmup_For_Fast_Feedback()
    {
        var quick = AutoTuneOptions.Quick;

        Assert.Equal(4, quick.BatchSize);
        Assert.Equal(2, quick.PlateauPatience);
        Assert.Equal(15, quick.MinSamples);
        Assert.Equal(2_000, quick.MaxSamples);
        Assert.Equal(0.05, quick.CiTarget);
        Assert.Equal(TimeSpan.FromMilliseconds(50), quick.MinMeasurementTime);

        // MaxTuningTime is 10s - half the Default cap. Warmup's share is WarmupBudgetFraction of
        // that (4s), which is 8x the inherited MinWarmupTime floor (500ms) and 2x the JIT-quiescence
        // gate's deactivation threshold (4 x MinWarmupTime). A tighter cap races the floor against
        // the budget and warns on warmup exhaustion.
        Assert.Equal(TimeSpan.FromSeconds(10), quick.MaxTuningTime);
    }

    [Fact]
    public void Quick_Preset_Does_Not_Shorten_The_Warmup_Time_Floor()
    {
        // The floor is a correctness requirement, not a speed/accuracy trade-off: a 25 ms floor
        // guarantees measuring pre-tier-1 code, which produced a 9.8x wrong number reported at
        // +/-0.86%. Quick's speed comes from CiTarget, MinSamples, and MaxTuningTime instead.
        Assert.Equal(TimeSpan.FromMilliseconds(500), AutoTuneOptions.Quick.MinWarmupTime);
        Assert.Equal(AutoTuneOptions.Default.MinWarmupTime, AutoTuneOptions.Quick.MinWarmupTime);
        Assert.Equal(AutoTuneOptions.Default.JitQuietPeriod, AutoTuneOptions.Quick.JitQuietPeriod);
    }

    [Fact]
    public void Thorough_Preset_Uses_50us_Target()
    {
        Assert.Equal(50_000, AutoTuneOptions.Thorough.TargetSampleDurationNs);
    }

    [Fact]
    public void Thorough_Preset_Raises_Every_Steady_State_Floor()
    {
        var t = AutoTuneOptions.Thorough;

        Assert.Equal(TimeSpan.FromMilliseconds(1_000), t.MinWarmupTime);
        Assert.Equal(TimeSpan.FromMilliseconds(100), t.JitQuietPeriod);
        Assert.Equal(TimeSpan.FromMilliseconds(500), t.MinMeasurementTime);
        Assert.Equal(20_000, t.MaxSamples);
        Assert.Equal(3, t.MeasurementRestartLimit);
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

    [Fact]
    public void JitQuietPeriod_Accepts_Zero_And_Rejects_Negative()
    {
        Assert.Equal(TimeSpan.Zero, new AutoTuneOptions { JitQuietPeriod = TimeSpan.Zero }.JitQuietPeriod);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AutoTuneOptions { JitQuietPeriod = TimeSpan.FromMilliseconds(-1) });
    }

    [Fact]
    public void MinMeasurementTime_Accepts_Zero_And_Rejects_Negative()
    {
        Assert.Equal(TimeSpan.Zero, new AutoTuneOptions { MinMeasurementTime = TimeSpan.Zero }.MinMeasurementTime);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AutoTuneOptions { MinMeasurementTime = TimeSpan.FromMilliseconds(-1) });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void MeasurementDriftTolerance_Accepts_Zero_To_One(double value)
    {
        Assert.Equal(value, new AutoTuneOptions { MeasurementDriftTolerance = value }.MeasurementDriftTolerance);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void MeasurementDriftTolerance_Rejects_Out_Of_Range(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AutoTuneOptions { MeasurementDriftTolerance = value });
    }

    [Fact]
    public void MeasurementRestartLimit_Accepts_Zero_And_Rejects_Negative()
    {
        Assert.Equal(0, new AutoTuneOptions { MeasurementRestartLimit = 0 }.MeasurementRestartLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoTuneOptions { MeasurementRestartLimit = -1 });
    }
}
