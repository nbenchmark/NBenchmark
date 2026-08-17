using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     <see cref="StatsPipeline.Run" />'s evidence-based interference-rejection pre-stage: the
///     rejected/statistical count split, the fold-in with the bimodal/GC-correlation warning, the
///     high-rejection-fraction warning, and the parity guarantee that a quiet run (nothing to reject)
///     reports exactly what it would have before this feature existed.
/// </summary>
public class StatsPipelineInterferenceTests
{
    private static double[] Constant(int n, double value = 100.0)
    {
        var timings = new double[n];
        Array.Fill(timings, value);
        return timings;
    }

    private static double[] UniformOccupancy(int n, double value = 1.0)
    {
        var occupancy = new double[n];
        Array.Fill(occupancy, value);
        return occupancy;
    }

    [Fact]
    public void Run_With_No_Interference_Evidence_Matches_The_Filter_Disabled_Path()
    {
        var timings = Constant(30);
        var occupancy = UniformOccupancy(30);

        var options = new MeasurementOptions { OutlierMode = OutlierMode.None };

        var withOccupancy = StatsPipeline.Run(timings, null, options, perSampleOccupancy: occupancy);
        var withoutOccupancy = StatsPipeline.Run(timings, null, options with { Interference = InterferenceOptions.Disabled });

        Assert.Equal(0, withOccupancy.InterferenceRejectedCount);
        Assert.Equal(withoutOccupancy.OutliersRemoved, withOccupancy.OutliersRemoved);
        Assert.Equal(withoutOccupancy.Stats.Mean, withOccupancy.Stats.Mean);
        Assert.Equal(withoutOccupancy.Stats.MarginOfError, withOccupancy.Stats.MarginOfError);
        Assert.Equal(withoutOccupancy.MeasuredIterations, withOccupancy.MeasuredIterations);
    }

    [Fact]
    public void Run_Rejects_A_Confirmed_Preempted_Sample_Before_Outlier_Trimming()
    {
        const int n = 30;
        var timings = Constant(n);
        var occupancy = UniformOccupancy(n);
        occupancy[10] = 0.01; // confirmed preempted

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.None },
            perSampleOccupancy: occupancy);

        Assert.Equal(1, result.InterferenceRejectedCount);
        Assert.Equal(n - 1, result.MeasuredIterations);
        Assert.Equal(1, result.OutliersRemoved);
    }

    [Fact]
    public void Run_Reports_The_Two_Discard_Counts_Separately_In_One_Folded_Warning()
    {
        const int n = 40;
        var timings = Constant(n);

        // One statistical outlier (far from the rest, no occupancy evidence) plus one confirmed
        // interference rejection (occupancy evidence, ordinary timing value).
        timings[0] = 100_000.0;

        var occupancy = UniformOccupancy(n);
        occupancy[20] = 0.01;

        var result = StatsPipeline.Run(
            timings, null, new MeasurementOptions { OutlierMode = OutlierMode.IqrFence },
            perSampleOccupancy: occupancy);

        Assert.Equal(1, result.InterferenceRejectedCount);

        var warning = Assert.Single(result.Warnings, w => w.Contains("confirmed preempted"));
        Assert.Contains("1 confirmed preempted", warning);
        Assert.Contains("statistical outlier", warning);

        // Not double-reported: exactly one warning mentions "confirmed preempted".
        Assert.Single(result.Warnings, w => w.Contains("confirmed preempted"));
    }

    [Fact]
    public void Run_Warns_When_The_Rejected_Fraction_Is_High()
    {
        const int n = 20;
        var timings = Constant(n);
        var occupancy = UniformOccupancy(n);

        // Reject 8 of 20 (40%), comfortably past the default 20% warning fraction.
        for (var i = 0; i < 8; i++)
        {
            occupancy[i] = 0.01;
        }

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.None },
            perSampleOccupancy: occupancy);

        Assert.Equal(8, result.InterferenceRejectedCount);
        Assert.Contains(result.Warnings, w => w.Contains("too noisy to trust"));
    }

    [Fact]
    public void Run_Does_Not_Warn_About_Noise_Below_The_High_Rejection_Fraction()
    {
        const int n = 30;
        var timings = Constant(n);
        var occupancy = UniformOccupancy(n);
        occupancy[0] = 0.01; // 1 of 30 (~3%), well under the default 20% floor

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.None },
            perSampleOccupancy: occupancy);

        Assert.Equal(1, result.InterferenceRejectedCount);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("too noisy to trust"));
    }

    [Fact]
    public void Run_TrimmedOrdinals_Point_At_The_Original_Raw_Array_After_Rejection()
    {
        const int n = 30;
        var timings = new double[n];

        for (var i = 0; i < n; i++)
        {
            timings[i] = 100.0;
        }

        // A genuine statistical outlier, positioned after the sample that gets rejected on
        // occupancy evidence - if remapping were wrong, this ordinal would point at the wrong raw
        // sample once the rejected one is spliced out ahead of it.
        timings[25] = 100_000.0;

        var occupancy = UniformOccupancy(n);
        occupancy[5] = 0.01;

        var result = StatsPipeline.Run(
            timings, null, new MeasurementOptions { OutlierMode = OutlierMode.IqrFence },
            perSampleOccupancy: occupancy);

        Assert.Contains(25, result.TrimmedOrdinals);
        Assert.DoesNotContain(5, result.TrimmedOrdinals); // rejected, not statistically trimmed
    }

    [Fact]
    public void Run_Reports_The_Median_Occupancy_Ratio()
    {
        const int n = 20;
        var timings = Constant(n);
        var occupancy = UniformOccupancy(n, 2.5);

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.None },
            perSampleOccupancy: occupancy);

        Assert.Equal(2.5, result.MedianOccupancyRatio);
    }

    [Fact]
    public void Run_Sets_A_Disabled_Reason_When_Most_Samples_Have_Unknown_Occupancy()
    {
        const int n = 30;
        var timings = Constant(n);
        var occupancy = new double[n];
        Array.Fill(occupancy, double.NaN);
        occupancy[0] = 1.0;

        var result = StatsPipeline.Run(timings, null, new MeasurementOptions { OutlierMode = OutlierMode.None },
            perSampleOccupancy: occupancy);

        Assert.Equal(0, result.InterferenceRejectedCount);
        Assert.Null(result.MedianOccupancyRatio);
        Assert.NotNull(result.InterferenceDisabledReason);
    }
}
