using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Direct unit tests for <see cref="OutlierTrim" />. The trim was previously
///     private to <c>BenchmarkRunner</c>; the most important test in this file
///     is <see cref="IqrFence_All_Filtered_Falls_Back_To_All_Values" />, which
///     pins the previously-untested all-filtered-fallback branch.
/// </summary>
public class OutlierTrimTests
{
    [Fact]
    public void None_Returns_Sorted_Input()
    {
        var values = new double[] { 5, 1, 3, 2, 4 };

        var result = OutlierTrim.Trim(values, OutlierMode.None);

        Assert.Equal(new double[] { 1, 2, 3, 4, 5 }, result);
    }

    [Fact]
    public void None_On_Empty_Array_Returns_Empty_Array()
    {
        var result = OutlierTrim.Trim([], OutlierMode.None);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(20, 19)]
    [InlineData(50, 47)]
    [InlineData(100, 95)]
    [InlineData(200, 190)]
    public void RemoveTop5Percent_Trims_Top5(int length, int expectedKept)
    {
        var values = Enumerable.Range(1, length).Select(i => (double)i).ToArray();

        var result = OutlierTrim.Trim(values, OutlierMode.RemoveTop5Percent);

        Assert.Equal(expectedKept, result.Length);
    }

    [Theory]
    [InlineData(20, 18)]
    [InlineData(50, 46)]
    [InlineData(100, 90)]
    [InlineData(200, 180)]
    public void RemoveBoth5Percent_Trims_5_Each_End(int length, int expectedKept)
    {
        var values = Enumerable.Range(1, length).Select(i => (double)i).ToArray();

        var result = OutlierTrim.Trim(values, OutlierMode.RemoveTop5PercentAndBottom5Percent);

        Assert.Equal(expectedKept, result.Length);
    }

    [Fact]
    public void IqrFence_Keeps_Inliers()
    {
        // No clear outliers; all values within 1.5 × IQR of Q1/Q3.
        var values = new double[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        var result = OutlierTrim.Trim(values, OutlierMode.IqrFence);

        Assert.Equal(values.Length, result.Length);
    }

    [Fact]
    public void IqrFence_Drops_Outliers()
    {
        // One extreme outlier (1000) among normal values; should be removed.
        var values = new double[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 1000 };

        var result = OutlierTrim.Trim(values, OutlierMode.IqrFence);

        Assert.DoesNotContain(1000.0, result);
        Assert.Equal(values.Length - 1, result.Length);
    }

    [Fact]
    public void IqrFence_All_Filtered_Falls_Back_To_All_Values()
    {
        // When every value is the same, IQR is 0 and the fence collapses to a
        // single point. No value is strictly "outside" the fence (every value
        // equals the fence), so the filter logic must fall back to returning
        // the input rather than an empty array. This branch was previously
        // untested.
        var values = new double[] { 42, 42, 42, 42, 42, 42, 42, 42 };

        var result = OutlierTrim.Trim(values, OutlierMode.IqrFence);

        Assert.Equal(values.Length, result.Length);
        Assert.All(result, v => Assert.Equal(42, v));
    }

    [Fact]
    public void IqrFence_Quartiles_Use_NearestRank()
    {
        // For 1..20 the nearest-rank percentile gives Q1 = 5, Q3 = 15
        // (numpy 'inverted_cdf'). Pin against the existing cross-check
        // contract — deliberately diverges from R's default type-7.
        var sorted = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        var q1 = Percentile.Compute(sorted, 0.25);
        var q3 = Percentile.Compute(sorted, 0.75);

        Assert.Equal(5.0, q1, 12);
        Assert.Equal(15.0, q3, 12);
        Assert.NotEqual(5.75, q1, 12);
        Assert.NotEqual(15.25, q3, 12);
    }
}
