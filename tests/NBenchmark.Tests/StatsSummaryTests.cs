using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class StatsSummaryTests
{
    [Fact]
    public void Compute_Returns_Correct_Values_For_Known_Data()
    {
        var samples = new double[] { 1, 2, 3, 4, 5 };
        Array.Sort(samples);
        var stats = StatsSummary.Compute(samples);

        Assert.Equal(3.0, stats.Mean, 10);
        Assert.Equal(3.0, stats.Median, 10);
        Assert.Equal(5.0, stats.P95, 10);
        Assert.Equal(5.0, stats.P99, 10);
        Assert.Equal(1.0, stats.Min, 10);
        Assert.Equal(5.0, stats.Max, 10);

        // Sample standard deviation (Bessel's correction): sqrt(10 / 4) = sqrt(2.5).
        Assert.Equal(Math.Sqrt(2.5), stats.StandardDeviation, 10);

        // Standard error = s / sqrt(n) = sqrt(2.5) / sqrt(5) = sqrt(0.5).
        Assert.Equal(Math.Sqrt(0.5), stats.StandardError, 10);
    }

    [Fact]
    public void Compute_Confidence_Interval_Is_Symmetric_And_Positive()
    {
        var samples = Enumerable.Range(1, 50).Select(i => (double)i).ToArray();
        Array.Sort(samples);
        var stats = StatsSummary.Compute(samples);

        Assert.Equal(0.95, stats.ConfidenceLevel, 10);
        Assert.True(stats.MarginOfError > 0);

        // Margin of error must equal t* × standard error and be a sane fraction of the spread.
        Assert.True(stats.MarginOfError < stats.StandardDeviation);
        Assert.True(stats.CoefficientOfVariation > 0);
    }

    [Fact]
    public void Compute_Higher_Confidence_Widens_Interval()
    {
        var samples = Enumerable.Range(1, 50).Select(i => (double)i).ToArray();
        Array.Sort(samples);

        var ninetyFive = StatsSummary.Compute(samples);
        var ninetyNine = StatsSummary.Compute(samples, 0.99);

        Assert.True(ninetyNine.MarginOfError > ninetyFive.MarginOfError);
    }

    [Fact]
    public void Compute_Empty_Array_Returns_Defaults()
    {
        var stats = StatsSummary.Compute([]);
        Assert.Equal(0, stats.Mean);
        Assert.Equal(0, stats.Median);
        Assert.Equal(0, stats.StandardDeviation);
        Assert.Equal(0, stats.StandardError);
        Assert.Equal(0, stats.MarginOfError);
    }

    [Fact]
    public void Compute_Single_Element()
    {
        var stats = StatsSummary.Compute([7.0]);
        Assert.Equal(7.0, stats.Mean, 10);
        Assert.Equal(7.0, stats.Median, 10);
        Assert.Equal(0.0, stats.StandardDeviation, 10);
        Assert.Equal(0.0, stats.StandardError, 10);
        Assert.Equal(0.0, stats.MarginOfError, 10);
    }

    [Fact]
    public void Compute_All_Identical_Values()
    {
        var samples = new double[] { 5, 5, 5, 5, 5 };
        Array.Sort(samples);
        var stats = StatsSummary.Compute(samples);

        Assert.Equal(5.0, stats.Mean, 10);
        Assert.Equal(5.0, stats.Median, 10);
        Assert.Equal(0.0, stats.StandardDeviation, 10);
    }
}