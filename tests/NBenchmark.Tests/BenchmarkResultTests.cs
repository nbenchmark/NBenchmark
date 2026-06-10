using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkResultTests
{
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
