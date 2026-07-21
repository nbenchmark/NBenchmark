using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class OutlierDetectorTests
{
    [Fact]
    public void ForMode_MapsMedianAbsoluteDeviation_ToMadDetector()
    {
        var detector = OutlierDetectors.ForMode(OutlierMode.MedianAbsoluteDeviation);

        Assert.IsType<MadOutlierDetector>(detector);
        Assert.Equal("MAD (3×)", detector.Name);
    }

    [Fact]
    public void Mad_DiscardsExtremeHighOutlier()
    {
        // n = 10 (even), so the median mid-averages the two middles: (18 + 20) / 2 = 19. The
        // absolute deviations from 19 are {1,1,3,3,5,5,7,7,9,181}, whose median is (5 + 5) / 2 = 5,
        // so scaled MAD = 5 × 1.4826 = 7.413 and the fence is 19 ± 22.239 → only 200 is outside.
        var values = new double[] { 10, 12, 14, 16, 18, 20, 22, 24, 26, 200 };

        var classification = new MadOutlierDetector().Classify(values);

        Assert.Equal([200d], classification.Discarded);
        Assert.Equal(9, classification.Kept.Length);
        Assert.Equal(26d, classification.Kept[^1]);
        Assert.NotNull(classification.LowerFence);
        Assert.NotNull(classification.UpperFence);
        Numerics.AssertRelativeClose(41.239, classification.UpperFence!.Value, 1e-4);
    }

    [Fact]
    public void Mad_KeepsEverything_WhenScaleIsZero()
    {
        // More than half the samples are identical, so the scaled MAD is 0: nothing to fence.
        var values = new double[] { 5, 5, 5, 5, 5, 5, 5, 99 };

        var classification = new MadOutlierDetector().Classify(values);

        Assert.Empty(classification.Discarded);
        Assert.Equal(values.Length, classification.Kept.Length);
    }

    [Fact]
    public void Mad_KeepsEverything_ForTinySamples()
    {
        var values = new double[] { 1, 100 };

        var classification = new MadOutlierDetector().Classify(values);

        Assert.Empty(classification.Discarded);
    }

    [Fact]
    public void Mad_DoesNotMutateInput()
    {
        var values = new double[] { 10, 12, 14, 16, 18, 20, 22, 24, 26, 200 };
        var copy = (double[])values.Clone();

        new MadOutlierDetector().Classify(values);

        Assert.Equal(copy, values);
    }

    [Fact]
    public void CustomThreshold_ChangesSensitivity()
    {
        var values = new double[] { 10, 12, 14, 16, 18, 20, 22, 24, 26, 40 };

        var looseDiscarded = new MadOutlierDetector(5.0).Classify(values).Discarded.Length;
        var tightDiscarded = new MadOutlierDetector(1.0).Classify(values).Discarded.Length;

        Assert.Equal(0, looseDiscarded);
        Assert.True(tightDiscarded > looseDiscarded);
        Assert.Equal("MAD (1×)", new MadOutlierDetector(1.0).Name);
    }

    [Fact]
    public void MeasurementOptions_ResolveOutlierDetector_PrefersCustomDetector()
    {
        var custom = new ThresholdOutlierDetector(100);

        var withCustom = MeasurementOptions.Default with { OutlierDetector = custom };
        var withModeOnly = MeasurementOptions.Default with { OutlierMode = OutlierMode.None };

        Assert.Same(custom, withCustom.ResolveOutlierDetector());
        Assert.IsType<NoOutlierDetector>(withModeOnly.ResolveOutlierDetector());
    }

    [Fact]
    public void OutlierTrim_UsesCustomDetector()
    {
        var values = new double[] { 1, 2, 3, 4, 5, 500 };

        var result = OutlierTrim.TrimDetailed(values, new ThresholdOutlierDetector(100));

        Assert.Equal([500d], result.Discarded);
        Assert.Equal(5, result.Kept.Length);

        // Quartiles are still computed by the engine regardless of the trimming strategy.
        Assert.True(result.InterquartileRange > 0);
    }

    [Fact]
    public void CustomDetector_NeedNotImplementFences()
    {
        var result = OutlierTrim.TrimDetailed([1, 2, 3, 4, 5, 500], new ThresholdOutlierDetector(100));

        Assert.Null(result.LowerFence);
        Assert.Null(result.UpperFence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void IqrFence_Throws_WhenKIsNotPositive(double k)
    {
        // k = 0 would collapse the fence to [Q1, Q3] - not a Tukey fence.
        Assert.Throws<ArgumentOutOfRangeException>(() => new IqrFenceOutlierDetector(k));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Mad_Throws_WhenThresholdIsNotPositive(double threshold) => Assert.Throws<ArgumentOutOfRangeException>(() => new MadOutlierDetector(threshold));

    /// <summary>A trivial custom detector that rejects any sample above a fixed cut-off.</summary>
    private sealed class ThresholdOutlierDetector(double cutoff) : IOutlierDetector
    {
        public string Name => $"threshold ({cutoff})";

        public OutlierClassification Classify(double[] sortedSamples)
        {
            var kept = sortedSamples.Where(v => v <= cutoff).ToArray();
            var discarded = sortedSamples.Where(v => v > cutoff).ToArray();

            return kept.Length == 0
                ? OutlierClassification.KeepAll(sortedSamples)
                : new OutlierClassification { Kept = kept, Discarded = discarded };
        }
    }
}
