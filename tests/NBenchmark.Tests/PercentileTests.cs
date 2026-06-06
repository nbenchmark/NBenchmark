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
        var sorted = new double[] { 1, 2, 3, 4 };
        var result = Percentile.Compute(sorted, 0.50);
        Assert.Equal(2, result);
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
