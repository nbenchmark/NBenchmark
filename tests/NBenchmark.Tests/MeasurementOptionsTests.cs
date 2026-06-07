using Xunit;

namespace NBenchmark.Tests;

public class MeasurementOptionsTests
{
    [Fact]
    public void Default_Has_Expected_Values()
    {
        var opts = MeasurementOptions.Default;

        Assert.Equal(25, opts.WarmupIterations);
        Assert.Equal(200, opts.Iterations);
        Assert.True(opts.ForceGcBeforeEachIteration);
        Assert.False(opts.MeasureAllocations);
        Assert.Equal(OutlierMode.RemoveTop5Percent, opts.OutlierMode);
        Assert.Equal(0.95, opts.ConfidenceLevel);
        Assert.True(opts.EnableSignificance);
        Assert.True(opts.ForceGcBetweenBenchmarks);
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