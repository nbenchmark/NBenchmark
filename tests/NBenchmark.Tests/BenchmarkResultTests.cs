using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkResultTests
{
    [Fact]
    public void CreateErrored_Sets_Errored_True_And_ErrorMessage()
    {
        var result = BenchmarkResult.CreateErrored("test", "something broke");

        Assert.True(result.Errored);
        Assert.Equal("test", result.Name);
        Assert.Equal("something broke", result.ErrorMessage);
        Assert.Equal(0, result.Mean);
        Assert.Equal(0, result.Median);
    }

    [Fact]
    public void CreateErrored_Defaults_Durations_To_Zero()
    {
        // Suite-setup-failure and other pre-runner catch sites call CreateErrored
        // without a timer. Pinned so the zero defaults stay documented.
        var result = BenchmarkResult.CreateErrored("test", "err");

        Assert.Equal(TimeSpan.Zero, result.TotalDuration);
        Assert.Equal(TimeSpan.Zero, result.MeasuredDuration);
    }

    [Fact]
    public void CreateErrored_Propagates_Provided_Durations()
    {
        var total = TimeSpan.FromMilliseconds(42);
        var measured = TimeSpan.FromMilliseconds(7);

        var result = BenchmarkResult.CreateErrored("test", "err",
            description: null, isBaseline: false,
            outlierMode: OutlierMode.RemoveTop5Percent,
            totalDuration: total, measuredDuration: measured);

        Assert.Equal(total, result.TotalDuration);
        Assert.Equal(measured, result.MeasuredDuration);
    }

    [Fact]
    public void CreateErrored_Sets_Description_And_Baseline()
    {
        var result = BenchmarkResult.CreateErrored("test", "err", "desc",
            true, OutlierMode.None);

        Assert.Equal("desc", result.Description);
        Assert.True(result.IsBaseline);
        Assert.Equal(OutlierMode.None, result.OutlierMode);
    }

    [Fact]
    public void ConfidenceInterval_Properties_Are_Computed()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            Mean = 100.0,
            Median = 100.0,
            P95 = 110.0,
            P99 = 120.0,
            Min = 80.0,
            Max = 130.0,
            StandardDeviation = 5.0,
            MarginOfError = 2.5,
        };

        Assert.Equal(97.5, result.ConfidenceIntervalLower);
        Assert.Equal(102.5, result.ConfidenceIntervalUpper);
    }

    [Fact]
    public void Default_OutlierMode_Is_RemoveTop5Percent()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            Mean = 0,
            Median = 0,
            P95 = 0,
            P99 = 0,
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
        };

        Assert.Equal(OutlierMode.RemoveTop5Percent, result.OutlierMode);
    }
}