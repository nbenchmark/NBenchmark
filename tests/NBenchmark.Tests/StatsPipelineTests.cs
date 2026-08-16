using NBenchmark.Engine;
using NBenchmark.Stats;
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

    // ---- Raw-basis tail statistics (P2-1) ---------------------------------

    // A bimodal stream: 90 fast samples plus a trimmed slow cluster. Under the default Raw
    // basis, Max/P99/histogram must include the slow cluster (the tail the fence exists to
    // describe), while Mean/StdDev exclude it (robust central statistics).
    private static double[] BimodalTimings()
    {
        var timings = new double[100];
        Array.Fill(timings, 100.0, 0, 90);
        Array.Fill(timings, 1000.0, 90, 10);
        return timings;
    }

    private static double PercentileValue(StatsSummary stats, double p) =>
        stats.Percentiles.First(e => Math.Abs(e.Percentile - p) < 1e-9).Value;

    [Fact]
    public void Run_RawTailBasis_Is_Default_And_Includes_Trimmed_Tail()
    {
        var result = StatsPipeline.Run(BimodalTimings(), null, new MeasurementOptions { Iterations = 100 });

        Assert.Equal(10, result.OutliersRemoved);

        // Central statistics stay on the trimmed set.
        Assert.Equal(100.0, result.Stats.Mean);
        Assert.Equal(0.0, result.Stats.StandardDeviation);

        // Order statistics describe the whole distribution.
        Assert.Equal(1000.0, result.Stats.Max);
        Assert.Equal(1000.0, PercentileValue(result.Stats, 0.99));
        Assert.Equal(1000.0, result.Stats.Histogram!.Max);
    }

    [Fact]
    public void Run_TrimmedTailBasis_Excludes_The_Trimmed_Tail()
    {
        var result = StatsPipeline.Run(BimodalTimings(), null,
            new MeasurementOptions { Iterations = 100, TailMetricsBasis = TailMetricsBasis.Trimmed });

        Assert.Equal(10, result.OutliersRemoved);
        Assert.Equal(100.0, result.Stats.Mean);
        Assert.Equal(100.0, result.Stats.Max);
        Assert.Equal(100.0, PercentileValue(result.Stats, 0.99));
        Assert.Equal(100.0, result.Stats.Histogram!.Max);
    }

    [Fact]
    public void Run_Populates_Median_Ci()
    {
        var timings = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var result = StatsPipeline.Run(timings, null,
            new MeasurementOptions { Iterations = 100, OutlierMode = OutlierMode.None });

        Assert.NotNull(result.Stats.MedianCiLower);
        Assert.NotNull(result.Stats.MedianCiUpper);
        Assert.True(result.Stats.MedianCiLower <= result.Stats.Median);
        Assert.True(result.Stats.MedianCiUpper >= result.Stats.Median);
    }

    // ---- GC <-> outlier annotation (P2-2) ---------------------------------

    [Fact]
    public void Run_Annotates_Gc_Correlated_Outliers()
    {
        // 35 fast + 5 slow (trimmed). n < 50 so the sample-quality checks stay out of the way.
        var timings = new double[40];
        Array.Fill(timings, 100.0, 0, 35);
        Array.Fill(timings, 1000.0, 35, 5);

        // Three of the five slow samples coincided with a collection.
        var gcCounts = new int[40];
        gcCounts[35] = 1;
        gcCounts[36] = 1;
        gcCounts[37] = 2;

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { Iterations = 40 }, gcCounts);

        Assert.Contains(result.Warnings, w => w.Contains("garbage collection"));
        Assert.Contains(result.Warnings, w => w.Contains("garbage collection") && w.Contains("3"));
    }

    [Fact]
    public void Run_Without_Gc_Counts_Omits_Gc_Annotation()
    {
        var timings = new double[40];
        Array.Fill(timings, 100.0, 0, 35);
        Array.Fill(timings, 1000.0, 35, 5);

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { Iterations = 40 });

        Assert.DoesNotContain(result.Warnings, w => w.Contains("garbage collection"));
    }

    // ---- Winsorized (Yuen) interval after trimming (W3) ---------------------

    /// <summary>
    ///     The reported interval is the Winsorized one, end to end. Trimming removed variance from
    ///     the sample set, and the interval has to keep it: the run produced <c>n</c> readings, not
    ///     the <c>h</c> that survived the fence.
    /// </summary>
    [Fact]
    public void Run_ReportedInterval_Is_Wider_When_The_Fence_Trimmed_Samples()
    {
        // A tight body with three preempted samples past the upper fence.
        var timings = new double[40];

        for (var i = 0; i < 37; i++)
        {
            timings[i] = 100.0 + i % 5;
        }

        timings[37] = 4_000.0;
        timings[38] = 4_500.0;
        timings[39] = 5_000.0;

        var options = new MeasurementOptions { Iterations = 40 };
        var trimmed = StatsPipeline.Run(timings, null, options);

        Assert.Equal(3, trimmed.OutliersRemoved);

        // What the naive formula would have reported: a plain t-interval on the kept samples alone.
        var detailed = OutlierTrim.TrimDetailed(timings, options.ResolveOutlierDetector());
        var naive = StatsSummary.Compute(detailed.Kept, options.ConfidenceLevel);

        Assert.Equal(naive.Mean, trimmed.Stats.Mean);
        Assert.Equal(naive.StandardDeviation, trimmed.Stats.StandardDeviation);
        Assert.True(trimmed.Stats.StandardError > naive.StandardError);
        Assert.True(trimmed.Stats.MarginOfError > naive.MarginOfError);

        // The recomputed Winsorized value is what it reports - not some other widening.
        var expected = WinsorizedError.Compute(TrimContext.From(detailed), options.ConfidenceLevel);

        Assert.NotNull(expected);
        Assert.Equal(expected.Value.StandardError, trimmed.Stats.StandardError);
        Assert.Equal(expected.Value.MarginOfError, trimmed.Stats.MarginOfError);
    }

    /// <summary>
    ///     The reduction property where it matters most: a run that trims nothing reports exactly the
    ///     numbers it reported before the correction existed. Exact equality, not a tolerance - the
    ///     estimator is defined to collapse, so anything else is a defect rather than rounding.
    /// </summary>
    [Fact]
    public void Run_With_OutlierMode_None_Reports_The_Unchanged_Naive_Interval()
    {
        var rng = new Random(4242);
        var timings = new double[200];

        for (var i = 0; i < timings.Length; i++)
        {
            timings[i] = 100.0 + rng.NextDouble() * 40.0;
        }

        var options = new MeasurementOptions { Iterations = 200, OutlierMode = OutlierMode.None };
        var result = StatsPipeline.Run(timings, null, options);

        var sorted = (double[])timings.Clone();
        Array.Sort(sorted);
        var naive = StatsSummary.Compute(sorted, options.ConfidenceLevel);

        Assert.Equal(0, result.OutliersRemoved);
        Assert.Equal(naive.StandardError, result.Stats.StandardError);
        Assert.Equal(naive.MarginOfError, result.Stats.MarginOfError);
    }

    /// <summary>
    ///     A fence that finds nothing to trim is the same case as no fence at all, and must report
    ///     the same interval - so the correction never fires on a clean run measured with the
    ///     defaults.
    /// </summary>
    [Fact]
    public void Run_With_A_Fence_That_Trims_Nothing_Reports_The_Unchanged_Naive_Interval()
    {
        var timings = Enumerable.Range(1, 60).Select(i => 100.0 + i % 7).ToArray();

        var fenced = StatsPipeline.Run(timings, null, new MeasurementOptions { Iterations = 60 });
        var untrimmed = StatsPipeline.Run(timings, null,
            new MeasurementOptions { Iterations = 60, OutlierMode = OutlierMode.None });

        Assert.Equal(0, fenced.OutliersRemoved);
        Assert.Equal(untrimmed.Stats.StandardError, fenced.Stats.StandardError);
        Assert.Equal(untrimmed.Stats.MarginOfError, fenced.Stats.MarginOfError);
    }
}
