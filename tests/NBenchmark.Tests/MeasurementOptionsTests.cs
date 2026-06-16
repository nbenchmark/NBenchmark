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
        Assert.True(opts.EnableSignificance);
        Assert.Equal(0.05, opts.SignificanceLevel);
        Assert.Null(opts.MinimumPracticalEffect);
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
}
