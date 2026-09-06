using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class PerformanceCalibrationTests
{
    [Fact]
    public void Run_Returns_Finite_Mean_And_Samples()
    {
        var result = PerformanceCalibration.Run();

        Assert.True(result.MeanNs > 0);
        Assert.True(result.MedianNs > 0);
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
        Assert.True(result.MeanNs > 0);
        Assert.True(result.MedianNs > 0);
        Assert.True(result.SampleCount > 0);
    }

    /// <summary>
    ///     The host's calibration says so. A gate that could not tell a host calibration from a
    ///     worker one would have no way to notice it was comparing across a process boundary, which is
    ///     the whole thing the isolation labelling exists to prevent.
    /// </summary>
    [Fact]
    public void The_Host_Calibration_Is_Labelled_As_Host_Measured()
    {
        Assert.False(PerformanceCalibration.CreateBenchmarkResult().IsolationStatus.IsIsolated());
    }

    /// <summary>
    ///     Host and worker measure the same code. Two definitions of the standard would drift, and a
    ///     ratio between a candidate and a divisor measuring different work is meaningless in a way
    ///     that produces no error.
    /// </summary>
    [Fact]
    public void The_Host_Calibration_Comes_From_The_Shared_Standard()
    {
        var direct = CalibrationStandard.Measure();
        var viaHost = PerformanceCalibration.Run();

        Assert.Equal(direct.Samples.Length, viaHost.Samples.Length);
    }

    [Fact]
    public void A_Worker_Calibration_Presented_As_A_Result_Is_Labelled_Isolated()
    {
        var measured = CalibrationStandard.Measure();
        var result = CalibrationStandard.ToBenchmarkResult(measured, IsolationStatus.Isolated);

        Assert.True(result.IsolationStatus.IsIsolated());
        Assert.Equal(measured.MedianNs, result.MedianNs);
    }
}
