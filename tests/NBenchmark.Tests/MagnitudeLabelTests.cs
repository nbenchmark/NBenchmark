using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class MagnitudeLabelTests
{
    [Theory]
    [InlineData(0.0, MagnitudeLabel.Negligible)]
    [InlineData(0.146, MagnitudeLabel.Negligible)]
    [InlineData(0.147, MagnitudeLabel.Small)]
    [InlineData(0.32, MagnitudeLabel.Small)]
    [InlineData(0.33, MagnitudeLabel.Medium)]
    [InlineData(0.473, MagnitudeLabel.Medium)]
    [InlineData(0.474, MagnitudeLabel.Large)]
    [InlineData(1.0, MagnitudeLabel.Large)]
    public void Classify_Applies_Romano_Thresholds(double absDelta, MagnitudeLabel expected) =>
        Assert.Equal(expected, MagnitudeLabelExtensions.Classify(absDelta));

    [Theory]
    [InlineData(MagnitudeLabel.Negligible, "neg")]
    [InlineData(MagnitudeLabel.Small, "small")]
    [InlineData(MagnitudeLabel.Medium, "med")]
    [InlineData(MagnitudeLabel.Large, "large")]
    public void ToShortString_Exhaustively_Maps_Each_Value(MagnitudeLabel label, string expected) => Assert.Equal(expected, label.ToShortString());

    [Fact]
    public void EffectSizeFactory_ForCliffsDelta_Populates_Default_Metadata()
    {
        var effect = EffectSizeFactory.ForCliffsDelta(0.62);

        Assert.Equal(EffectMetrics.CliffsDelta, effect.Metric);
        Assert.Equal(0.62, effect.Value);
        Assert.Equal(MagnitudeLabel.Large, effect.Magnitude);
        Assert.Equal(EffectDirection.CandidateHigher, effect.Direction);
        Assert.Equal(0.62, effect.PracticalValue);
    }
}
