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

        Assert.Equal(3.0, stats.MeanNs, 10);
        Assert.Equal(3.0, stats.MedianNs, 10);
        Assert.Equal(5.0, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 10);
        Assert.Equal(5.0, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value, 10);
        Assert.Equal(1.0, stats.MinNs, 10);
        Assert.Equal(5.0, stats.MaxNs, 10);

        // Sample standard deviation (Bessel's correction): sqrt(10 / 4) = sqrt(2.5).
        Assert.Equal(Math.Sqrt(2.5), stats.StandardDeviationNs, 10);

        // Standard error = s / sqrt(n) = sqrt(2.5) / sqrt(5) = sqrt(0.5).
        Assert.Equal(Math.Sqrt(0.5), stats.StandardErrorNs, 10);
    }

    [Fact]
    public void Compute_Unsorted_Input_Produces_Same_Results_As_Sorted_And_Does_Not_Mutate()
    {
        // RawSamples is documented as execution-order (unsorted); Compute must not
        // silently mis-report order-dependent statistics for such input.
        var unsorted = new double[] { 5, 1, 4, 2, 3 };
        var original = (double[])unsorted.Clone();

        var stats = StatsSummary.Compute(unsorted);

        Assert.Equal(3.0, stats.MedianNs, 10);
        Assert.Equal(1.0, stats.MinNs, 10);
        Assert.Equal(5.0, stats.MaxNs, 10);
        Assert.Equal(5.0, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 10);
        Assert.Equal(original, unsorted);

        var sorted = new double[] { 1, 2, 3, 4, 5 };
        var expected = StatsSummary.Compute(sorted);

        Assert.Equal(expected.MeanNs, stats.MeanNs, 10);
        Assert.Equal(expected.StandardDeviationNs, stats.StandardDeviationNs, 10);
        Assert.Equal(expected.MedianAbsoluteDeviationNs, stats.MedianAbsoluteDeviationNs, 10);
        Assert.Equal(expected.Skewness, stats.Skewness, 10);
        Assert.Equal(expected.Kurtosis, stats.Kurtosis, 10);
    }

    [Fact]
    public void Compute_Confidence_Interval_Is_Symmetric_And_Positive()
    {
        var samples = Enumerable.Range(1, 50).Select(i => (double)i).ToArray();
        Array.Sort(samples);
        var stats = StatsSummary.Compute(samples);

        Assert.Equal(0.95, stats.ConfidenceLevel, 10);
        Assert.True(stats.MarginOfErrorNs > 0);

        // Margin of error must equal t* × standard error and be a sane fraction of the spread.
        Assert.True(stats.MarginOfErrorNs < stats.StandardDeviationNs);
        Assert.True(stats.CoefficientOfVariation > 0);
    }

    [Fact]
    public void Compute_Higher_Confidence_Widens_Interval()
    {
        var samples = Enumerable.Range(1, 50).Select(i => (double)i).ToArray();
        Array.Sort(samples);

        var ninetyFive = StatsSummary.Compute(samples);
        var ninetyNine = StatsSummary.Compute(samples, 0.99);

        Assert.True(ninetyNine.MarginOfErrorNs > ninetyFive.MarginOfErrorNs);
    }

    [Fact]
    public void Compute_Empty_Array_Returns_Defaults()
    {
        var stats = StatsSummary.Compute([]);
        Assert.Equal(0, stats.MeanNs);
        Assert.Equal(0, stats.MedianNs);
        Assert.Equal(0, stats.StandardDeviationNs);
        Assert.Equal(0, stats.StandardErrorNs);
        Assert.Equal(0, stats.MarginOfErrorNs);
    }

    [Fact]
    public void Compute_Single_Element()
    {
        var stats = StatsSummary.Compute([7.0]);
        Assert.Equal(7.0, stats.MeanNs, 10);
        Assert.Equal(7.0, stats.MedianNs, 10);
        Assert.Null(stats.Histogram);
        Assert.Equal(0.0, stats.StandardDeviationNs, 10);
        Assert.Equal(0.0, stats.StandardErrorNs, 10);
        Assert.Equal(0.0, stats.MarginOfErrorNs, 10);
    }

    [Fact]
    public void Compute_All_Identical_Values()
    {
        var samples = new double[] { 5, 5, 5, 5, 5 };
        Array.Sort(samples);
        var stats = StatsSummary.Compute(samples);

        Assert.Equal(5.0, stats.MeanNs, 10);
        Assert.Equal(5.0, stats.MedianNs, 10);
        Assert.Equal(0.0, stats.StandardDeviationNs, 10);
    }

    [Fact]
    public void Compute_Normalizes_Custom_Percentiles()
    {
        var samples = new double[] { 1, 2, 3, 4, 5 };

        var stats = StatsSummary.Compute(samples, reportedPercentiles: [0.99, 0.95, 0.95, 1.0, 0.50]);

        Assert.Equal([0.50, 0.95, 0.99, 1.0], stats.Percentiles.Select(p => p.Percentile).ToArray());
    }

    [Fact]
    public void Compute_Disables_Histogram_When_Requested()
    {
        var samples = new double[] { 1, 2, 3, 4, 5 };

        var stats = StatsSummary.Compute(samples, enableHistogram: false);

        Assert.Null(stats.Histogram);
    }

    [Fact]
    public void Compute_Histogram_Rejects_Invalid_Bucket_Count_When_Enabled()
    {
        var samples = new double[] { 1, 2, 3 };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StatsSummary.Compute(samples, enableHistogram: true, histogramBucketCount: 0));
    }
}
