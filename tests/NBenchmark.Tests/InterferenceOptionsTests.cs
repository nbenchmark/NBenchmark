using Xunit;

namespace NBenchmark.Tests;

public class InterferenceOptionsTests
{
    [Fact]
    public void Default_Is_Enabled_With_Documented_Defaults()
    {
        var options = InterferenceOptions.Default;

        Assert.True(options.Enabled);
        Assert.Equal(InterferenceOptions.DefaultRejectionThreshold, options.RejectionThreshold);
        Assert.Equal(InterferenceOptions.DefaultProbeCostBudgetFraction, options.ProbeCostBudgetFraction);
        Assert.Equal(InterferenceOptions.DefaultKnownSampleFraction, options.KnownSampleFraction);
        Assert.Equal(InterferenceOptions.DefaultHighRejectionWarningFraction, options.HighRejectionWarningFraction);
    }

    [Fact]
    public void Disabled_Preset_Turns_The_Filter_Off()
    {
        Assert.False(InterferenceOptions.Disabled.Enabled);
    }

    [Theory]
    [InlineData(InterferenceOptions.MinRejectionThreshold)]
    [InlineData(InterferenceOptions.MaxRejectionThreshold)]
    [InlineData(0.5)]
    public void RejectionThreshold_Accepts_The_Documented_Range(double value)
    {
        var options = new InterferenceOptions { RejectionThreshold = value };
        Assert.Equal(value, options.RejectionThreshold);
    }

    [Theory]
    [InlineData(InterferenceOptions.MinRejectionThreshold - 0.001)]
    [InlineData(InterferenceOptions.MaxRejectionThreshold + 0.001)]
    public void RejectionThreshold_Rejects_Out_Of_Range_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InterferenceOptions { RejectionThreshold = value });
    }

    [Theory]
    [InlineData(InterferenceOptions.MinProbeCostBudgetFraction - 0.00001)]
    [InlineData(InterferenceOptions.MaxProbeCostBudgetFraction + 0.001)]
    public void ProbeCostBudgetFraction_Rejects_Out_Of_Range_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InterferenceOptions { ProbeCostBudgetFraction = value });
    }

    [Theory]
    [InlineData(InterferenceOptions.MinKnownSampleFraction - 0.001)]
    [InlineData(InterferenceOptions.MaxKnownSampleFraction + 0.001)]
    public void KnownSampleFraction_Rejects_Out_Of_Range_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InterferenceOptions { KnownSampleFraction = value });
    }

    [Theory]
    [InlineData(InterferenceOptions.MinHighRejectionWarningFraction - 0.001)]
    [InlineData(InterferenceOptions.MaxHighRejectionWarningFraction + 0.001)]
    public void HighRejectionWarningFraction_Rejects_Out_Of_Range_Values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InterferenceOptions { HighRejectionWarningFraction = value });
    }

    [Fact]
    public void MeasurementOptions_Defaults_To_Interference_Enabled()
    {
        Assert.True(new MeasurementOptions().Interference.Enabled);
    }
}
