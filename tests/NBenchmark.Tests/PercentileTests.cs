using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class PercentileTests
{
    [Fact]
    public void Compute_Median_Of_Sorted_Odd_Array()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5 };
        var result = Percentile.Compute(sorted, 0.50);
        Assert.Equal(3, result);
    }

    [Fact]
    public void Compute_Median_Of_Sorted_Even_Array()
    {
        // The median (p == 0.50) uses the mid-average convention: the mean of the two middle
        // order statistics on even n. For {1, 2, 3, 4} that is (2 + 3) / 2 = 2.5, not the
        // nearest-rank lower-middle (2). Other percentiles keep the nearest-rank convention.
        var sorted = new double[] { 1, 2, 3, 4 };
        var result = Percentile.Compute(sorted, 0.50);
        Assert.Equal(2.5, result);
    }

    [Fact]
    public void Compute_Median_MidAverage_Matches_Other_Median_Conventions()
    {
        // Regression guard for the unified median: p == 0.50 must average the two middles so the
        // reported Median agrees with JitterCalibrator.Median / LaunchAggregator.MedianOf.
        var sorted = new double[] { 10, 20, 30, 40, 50, 60 };
        Assert.Equal(35, Percentile.Compute(sorted, 0.50));
    }

    [Fact]
    public void Compute_P95()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var result = Percentile.Compute(sorted, 0.95);
        Assert.Equal(10, result);
    }

    [Fact]
    public void Compute_P99()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var result = Percentile.Compute(sorted, 0.99);
        Assert.Equal(10, result);
    }

    [Fact]
    public void Compute_Empty_Array_Returns_Zero()
    {
        var result = Percentile.Compute([], 0.50);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Compute_Single_Element()
    {
        var result = Percentile.Compute([42], 0.50);
        Assert.Equal(42, result);
    }
}
