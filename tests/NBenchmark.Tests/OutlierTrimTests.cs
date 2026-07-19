using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

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

        var result = OutlierTrim.Trim(values, OutlierMode.RemoveTopAndBottom5Percent);

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
        // the input rather than an empty array.
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
        // contract - deliberately diverges from R's default type-7.
        var sorted = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        var q1 = Percentile.Compute(sorted, 0.25);
        var q3 = Percentile.Compute(sorted, 0.75);

        Assert.Equal(5.0, q1, 12);
        Assert.Equal(15.0, q3, 12);
        Assert.NotEqual(5.75, q1, 12);
        Assert.NotEqual(15.25, q3, 12);
    }

    [Fact]
    public void TrimDetailed_NoOutliers_YieldsEmptyTrimmedOrdinals()
    {
        var values = new double[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        var result = OutlierTrim.TrimDetailed(values, OutlierMode.IqrFence);

        Assert.Empty(result.TrimmedOrdinals);
    }

    [Fact]
    public void TrimDetailed_SingleOutlier_RecordsCorrectOrdinal()
    {
        // The trailing 1000 is the only outlier; its original position is index 11.
        var values = new double[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 1000 };

        var result = OutlierTrim.TrimDetailed(values, OutlierMode.IqrFence);

        Assert.Single(result.TrimmedOrdinals);
        Assert.Equal(11, result.TrimmedOrdinals[0]);
        Assert.Single(result.Discarded);
        Assert.Equal(1000d, result.Discarded[0]);
    }

    [Fact]
    public void TrimDetailed_DuplicateOutliers_ReceiveDistinctOrdinals()
    {
        // Two identical low outliers (5 at indices 1 and 3) and two identical
        // high outliers (100 at indices 5 and 7). The IQR fence rejects both
        // copies of each. The recovery walk must produce four distinct
        // ordinals - one per discarded sample - even though the values repeat.
        var values = new double[]
        {
            50, 5, 50, 5, 50, 100, 50, 100, 50, 50, 50, 50,
        };

        var result = OutlierTrim.TrimDetailed(values, OutlierMode.IqrFence);

        // Exactly four samples were discarded.
        Assert.Equal(4, result.Discarded.Length);
        Assert.Equal(4, result.TrimmedOrdinals.Length);

        // Every ordinal is distinct and points back at a discarded value.
        var ordinalSet = new HashSet<int>(result.TrimmedOrdinals);
        Assert.Equal(4, ordinalSet.Count);

        foreach (var ordinal in result.TrimmedOrdinals)
        {
            var v = values[ordinal];
            Assert.True(v == 5d || v == 100d);
        }

        // The two low outliers both read 5 and the two high outliers both read 100.
        Assert.Equal(2, result.TrimmedOrdinals.Count(o => values[o] == 5d));
        Assert.Equal(2, result.TrimmedOrdinals.Count(o => values[o] == 100d));
    }

    [Fact]
    public void TrimDetailed_DuplicateValue_PartlyKeptPartlyDiscarded()
    {
        // A fence that lands such that one copy of a duplicated value is kept
        // and another discarded: the lockstep walk must consume exactly one
        // sorted slot per discarded entry, leaving the remaining copies for the
        // kept set. Use a custom detector to control the partition precisely.
        // Sorted input: [10, 20, 20, 20, 30]. Discard exactly two of the 20s.
        // After sorting: indices [0,1,2,3,4] -> values [10,20,20,20,30].
        // Original positions: value 10 at 0, value 20 at indices 1,2,3, value 30 at 4.
        // We build the input in arrival order [20, 10, 20, 30, 20] so the original
        // ordinals of the 20s are {0, 2, 4}. Discarding two of the three 20s must
        // return two distinct ordinals from that set.
        var values = new double[] { 20, 10, 20, 30, 20 };

        var result = OutlierTrim.TrimDetailed(values, new DiscardTwoTwentiesDetector());

        Assert.Equal(2, result.Discarded.Length);
        Assert.Equal(2, result.TrimmedOrdinals.Length);

        Assert.All(result.Discarded, v => Assert.Equal(20d, v));

        var ordinalSet = new HashSet<int>(result.TrimmedOrdinals);
        Assert.Equal(2, ordinalSet.Count);
        Assert.Subset(new HashSet<int> { 0, 2, 4 }, ordinalSet);
    }

    [Fact]
    public void TrimDetailed_CustomDetector_DeduplicatesDiscarded_ResizeToFound()
    {
        // A pathological detector that returns fewer discarded values than the
        // input actually contains (it deduplicates). The recovery walk trims
        // its result array to the number of matches it actually found, keeping
        // the invariant result.Length == discarded.Length.
        var values = new double[] { 10, 100, 20, 100, 30 };

        var result = OutlierTrim.TrimDetailed(values, new DeduplicatingDetector());

        // The detector returns a single 100 in Discarded even though two are
        // present. TrimmedOrdinals must therefore have length 1.
        Assert.Single(result.Discarded);
        Assert.Single(result.TrimmedOrdinals);
        Assert.Contains(result.TrimmedOrdinals[0], new[] { 1, 3 });
    }

    private sealed class DiscardTwoTwentiesDetector : IOutlierDetector
    {
        public string Name => "Discard two 20s";

        public OutlierClassification Classify(double[] sortedSamples)
        {
            // sortedSamples is sorted ascending: [10, 20, 20, 20, 30].
            // Keep [10, 20, 30]; discard two of the 20s.
            var kept = new List<double> { sortedSamples[0], sortedSamples[1], sortedSamples[4] };
            var discarded = new[] { sortedSamples[2], sortedSamples[3] };

            return new OutlierClassification
            {
                Kept = kept.ToArray(),
                Discarded = discarded,
            };
        }
    }

    private sealed class DeduplicatingDetector : IOutlierDetector
    {
        public string Name => "Deduplicating";

        public OutlierClassification Classify(double[] sortedSamples)
        {
            // sortedSamples is sorted ascending: [10, 20, 30, 100, 100].
            // "Discard" the value 100 but only report it once (deduplicating).
            var kept = new double[] { 10, 20, 30, 100 };
            var discarded = new double[] { 100 };

            return new OutlierClassification
            {
                Kept = kept,
                Discarded = discarded,
            };
        }
    }
}
