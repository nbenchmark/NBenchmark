using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     <see cref="InterferenceRejector" /> - the evidence-based rejection stage, tested entirely on
///     synthetic occupancy arrays rather than the real thread-CPU clock. This is the seam the plan
///     asks for: the ratio/rejection logic and the graceful-degradation paths are what CI can
///     actually exercise regardless of which of Linux/macOS/Windows it runs on, since none of this
///     touches the P/Invoke layer.
/// </summary>
public class InterferenceRejectorTests
{
    private static double[] Timings(int count, double value = 100.0)
    {
        var timings = new double[count];
        Array.Fill(timings, value);
        return timings;
    }

    [Fact]
    public void Reject_Disabled_Returns_Everything_Unchanged()
    {
        var timings = Timings(20);
        var occupancy = new double[20];
        Array.Fill(occupancy, 1.0);
        occupancy[5] = 0.01; // would be rejected if the filter were on

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Disabled);

        Assert.Equal(timings.Length, result.SurvivingTimings.Length);
        Assert.Empty(result.RejectedOriginalIndices);
        Assert.Null(result.MedianOccupancyRatio);
        Assert.Null(result.DisabledReason);
    }

    [Fact]
    public void Reject_Null_Occupancy_Is_A_NoOp()
    {
        var timings = Timings(20);

        var result = InterferenceRejector.Reject(timings, null, InterferenceOptions.Default);

        Assert.Equal(timings.Length, result.SurvivingTimings.Length);
        Assert.Empty(result.RejectedOriginalIndices);
        Assert.Null(result.MedianOccupancyRatio);
    }

    [Fact]
    public void Reject_Mismatched_Occupancy_Length_Is_A_NoOp()
    {
        var timings = Timings(20);
        var occupancy = new double[5];

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.Equal(timings.Length, result.SurvivingTimings.Length);
        Assert.Empty(result.RejectedOriginalIndices);
    }

    [Fact]
    public void Reject_Discards_A_Sample_With_Occupancy_Far_Below_The_Median()
    {
        const int n = 30;
        var timings = Timings(n);
        var occupancy = new double[n];
        Array.Fill(occupancy, 1.0);

        // One sample held the CPU for only 10% of what every other sample did - well below the
        // default 0.5 * median threshold.
        occupancy[10] = 0.1;

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.Equal([10], result.RejectedOriginalIndices);
        Assert.Equal(n - 1, result.SurvivingTimings.Length);
        Assert.Equal(1.0, result.MedianOccupancyRatio);
        Assert.Null(result.DisabledReason);
    }

    [Fact]
    public void Reject_Keeps_A_Sample_At_Exactly_The_Threshold()
    {
        const int n = 30;
        var timings = Timings(n);
        var occupancy = new double[n];
        Array.Fill(occupancy, 1.0);

        // Exactly at threshold * median (0.5): the rule is strictly-less-than, so this must survive.
        occupancy[10] = 0.5;

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.Empty(result.RejectedOriginalIndices);
        Assert.Equal(n, result.SurvivingTimings.Length);
    }

    [Fact]
    public void Reject_Never_Rejects_A_NaN_Occupancy_Sample()
    {
        const int n = 30;
        var timings = Timings(n);
        var occupancy = new double[n];
        Array.Fill(occupancy, 1.0);

        // NaN ("unknown occupancy", e.g. a thread-hopped async sample) must never be rejected, no
        // matter how low the threshold. It also must not count toward - or distort - the median.
        occupancy[0] = double.NaN;
        occupancy[1] = double.NaN;

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.Empty(result.RejectedOriginalIndices);
        Assert.Equal(n, result.SurvivingTimings.Length);
        Assert.Equal(1.0, result.MedianOccupancyRatio);
    }

    [Fact]
    public void Reject_Excludes_NaN_Samples_From_The_Median_Computation()
    {
        const int n = 30;
        var timings = Timings(n);
        var occupancy = new double[n];

        // Half the known readings are 1.0, half are 0.2, with a handful unknown - the known
        // fraction (25 of 30, well above the default 50% floor) still lets the stage run, so the
        // median has to come from the known values only, not treat NaN as 0.
        for (var i = 0; i < n; i++)
        {
            occupancy[i] = i < 5 ? double.NaN : (i % 2 == 0 ? 1.0 : 0.2);
        }

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.NotNull(result.MedianOccupancyRatio);
        Assert.True(result.MedianOccupancyRatio > 0);
    }

    [Fact]
    public void Reject_Disables_Itself_When_Most_Samples_Have_Unknown_Occupancy()
    {
        const int n = 30;
        var timings = Timings(n);
        var occupancy = new double[n];

        // Only 2 of 30 samples have a known ratio - well below the default 50% floor. This is the
        // "async continuations mostly resumed on a different thread" case from the plan's 1.4.
        Array.Fill(occupancy, double.NaN);
        occupancy[0] = 1.0;
        occupancy[1] = 1.0;

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.Empty(result.RejectedOriginalIndices);
        Assert.Equal(n, result.SurvivingTimings.Length);
        Assert.Null(result.MedianOccupancyRatio);
        Assert.NotNull(result.DisabledReason);
    }

    [Fact]
    public void Reject_Disables_Itself_When_Too_Few_Samples_Were_Measured()
    {
        // Below the internal absolute floor even though every sample has a known ratio: two or
        // three readings cannot support a trustworthy median.
        var timings = Timings(5);
        var occupancy = new double[5];
        Array.Fill(occupancy, 1.0);
        occupancy[0] = 0.01;

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.Empty(result.RejectedOriginalIndices);
        Assert.NotNull(result.DisabledReason);
    }

    [Fact]
    public void Reject_Surviving_Indices_Map_Back_To_The_Original_Array()
    {
        const int n = 30;
        var timings = new double[n];

        for (var i = 0; i < n; i++)
        {
            timings[i] = i; // distinct values so identity is checkable
        }

        var occupancy = new double[n];
        Array.Fill(occupancy, 1.0);
        occupancy[15] = 0.01;

        var result = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);

        Assert.DoesNotContain(15.0, result.SurvivingTimings);
        Assert.Equal(result.SurvivingTimings.Length, result.SurvivingOriginalIndices.Length);

        for (var i = 0; i < result.SurvivingTimings.Length; i++)
        {
            Assert.Equal(timings[result.SurvivingOriginalIndices[i]], result.SurvivingTimings[i]);
        }
    }

    [Fact]
    public void Reject_Honors_A_Custom_RejectionThreshold()
    {
        const int n = 30;
        var timings = Timings(n);
        var occupancy = new double[n];
        Array.Fill(occupancy, 1.0);
        occupancy[5] = 0.85; // below a strict 0.9 threshold, above the default 0.5

        var strict = InterferenceOptions.Default with { RejectionThreshold = 0.9 };
        var result = InterferenceRejector.Reject(timings, occupancy, strict);

        Assert.Equal([5], result.RejectedOriginalIndices);

        var defaultResult = InterferenceRejector.Reject(timings, occupancy, InterferenceOptions.Default);
        Assert.Empty(defaultResult.RejectedOriginalIndices);
    }
}
