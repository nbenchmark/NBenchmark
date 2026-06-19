using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class StatsPipelineTests
{
    [Fact]
    public void Run_With_All_Same_Values_Returns_Zero_StdDev()
    {
        var timings = new double[50];
        Array.Fill(timings, 100.0);

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { Iterations = 50, OutlierMode = OutlierMode.None });

        Assert.Equal(100.0, result.Stats.Mean);
        Assert.Equal(100.0, result.Stats.Median);
        Assert.Equal(0, result.Stats.StandardDeviation);
        Assert.Equal(100, result.Stats.Min);
        Assert.Equal(100, result.Stats.Max);
        Assert.Equal(50, result.MeasuredIterations);
    }

    [Fact]
    public void Run_With_Allocations_Averages_Into_MeanAllocatedBytes()
    {
        var timings = new double[] { 1, 2, 3, 4 };
        var allocations = new long[] { 100, 200, 300, 400 };

        var result = StatsPipeline.Run(timings, allocations, new MeasurementOptions());

        Assert.Equal(250, result.MeanAllocatedBytes);
    }

    [Fact]
    public void Run_Without_Allocations_Sets_MeanAllocatedBytes_Null()
    {
        var timings = new double[] { 1, 2, 3, 4 };

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions());

        Assert.Null(result.MeanAllocatedBytes);
    }

    [Fact]
    public void Run_ConfidenceLevel_Plumbs_Through_To_Stats()
    {
        var timings = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { ConfidenceLevel = 0.99 });

        Assert.Equal(0.99, result.Stats.ConfidenceLevel);
    }

    [Fact]
    public void Run_MeasuredIterations_Equals_Trimmed_Length_None()
    {
        var timings = new double[100];
        Array.Fill(timings, 1.0);

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.None });

        Assert.Equal(100, result.MeasuredIterations);
    }

    [Fact]
    public void Run_MeasuredIterations_Equals_Trimmed_Length_RemoveTop5()
    {
        var timings = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.RemoveTop5Percent });

        Assert.Equal(95, result.MeasuredIterations);
    }

    [Fact]
    public void Run_MeasuredIterations_Equals_Trimmed_Length_Both()
    {
        var timings = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.RemoveTopAndBottom5Percent });

        Assert.Equal(90, result.MeasuredIterations);
    }

    [Fact]
    public void Run_Empty_Timings_Returns_Zero_Stats()
    {
        var result = StatsPipeline.Run([], null, new MeasurementOptions());

        Assert.Equal(0, result.Stats.Mean);
        Assert.Equal(0, result.Stats.Median);
        Assert.Equal(0, result.Stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value);
        Assert.Equal(0, result.Stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value);
        Assert.Equal(0, result.Stats.Min);
        Assert.Equal(0, result.Stats.Max);
        Assert.Equal(0, result.Stats.StandardDeviation);
        Assert.Equal(0, result.Stats.StandardError);
        Assert.Equal(0, result.Stats.MarginOfError);
        Assert.Equal(0, result.MeasuredIterations);
        Assert.Null(result.MeanAllocatedBytes);
    }

    [Fact]
    public void Run_Unsorted_Input_Still_Produces_Correct_Stats()
    {
        // The pipeline sorts via trim; input order must not affect the result.
        var unsorted = new double[] { 5, 2, 8, 1, 9, 3, 7, 4, 6 };
        var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        var unsortedResult = StatsPipeline.Run(unsorted, null, new MeasurementOptions { OutlierMode = OutlierMode.None });
        var sortedResult = StatsPipeline.Run(sorted, null, new MeasurementOptions { OutlierMode = OutlierMode.None });

        Assert.Equal(sortedResult.Stats.Mean, unsortedResult.Stats.Mean);
        Assert.Equal(sortedResult.Stats.Median, unsortedResult.Stats.Median);
        Assert.Equal(sortedResult.Stats.Min, unsortedResult.Stats.Min);
        Assert.Equal(sortedResult.Stats.Max, unsortedResult.Stats.Max);
    }

    [Theory]
    [InlineData(OutlierMode.None)]
    [InlineData(OutlierMode.IqrFence)]
    public void Run_Does_Not_Mutate_RawTimings_Input(OutlierMode mode)
    {
        var rawTimings = new double[] { 5, 2, 8, 1, 9, 3, 7, 4, 6 };
        var snapshot = (double[])rawTimings.Clone();

        _ = StatsPipeline.Run(rawTimings, null, new MeasurementOptions { OutlierMode = mode });

        Assert.Equal(snapshot, rawTimings);
    }
}
