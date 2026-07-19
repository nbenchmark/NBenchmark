using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class PerformanceCalibrationTests
{
    [Fact]
    public void Run_Returns_Finite_Mean_And_Samples()
    {
        var result = PerformanceCalibration.Run();

        Assert.True(result.Mean > 0);
        Assert.True(result.Median > 0);
        Assert.NotEmpty(result.Samples);
    }

    [Fact]
    public void Run_Caches_Across_Calls()
    {
        var first = PerformanceCalibration.Run();
        var second = PerformanceCalibration.Run();

        Assert.Same(first, second);
    }

    [Fact]
    public void CreateBenchmarkResult_Returns_Valid_Result()
    {
        var result = PerformanceCalibration.CreateBenchmarkResult();

        Assert.Equal("calibration", result.Name);
        Assert.True(result.Mean > 0);
        Assert.True(result.Median > 0);
        Assert.True(result.N > 0);
    }
}
