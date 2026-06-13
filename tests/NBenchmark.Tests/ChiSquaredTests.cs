using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class ChiSquaredTests
{
    [Fact]
    public void SurvivalFunction_AtZero_IsOne()
    {
        Assert.Equal(1.0, ChiSquared.SurvivalFunction(0, 1), 10);
        Assert.Equal(1.0, ChiSquared.SurvivalFunction(0, 5), 10);
    }

    [Fact]
    public void SurvivalFunction_NegativeStatistic_IsOne()
    {
        Assert.Equal(1.0, ChiSquared.SurvivalFunction(-3, 2), 10);
    }

    [Fact]
    public void SurvivalFunction_InvalidDegreesOfFreedom_IsNaN()
    {
        Assert.True(double.IsNaN(ChiSquared.SurvivalFunction(1.0, 0)));
        Assert.True(double.IsNaN(ChiSquared.SurvivalFunction(1.0, -1)));
    }

    [Fact]
    public void SurvivalFunction_TwoDegreesOfFreedom_MatchesExponentialClosedForm()
    {
        // The chi-squared distribution with 2 df is Exp(mean = 2), so SF(x) = e^(-x/2).
        foreach (var x in new[] { 0.5, 1.0, 2.0, 4.0, 6.0, 9.0 })
            Numerics.AssertRelativeClose(Math.Exp(-x / 2.0), ChiSquared.SurvivalFunction(x, 2), 1e-9);
    }

    [Fact]
    public void SurvivalFunction_FourDegreesOfFreedom_MatchesClosedForm()
    {
        // For 4 df, SF(x) = (1 + x/2) e^(-x/2).
        foreach (var x in new[] { 1.0, 3.0, 4.0, 7.5 })
        {
            var expected = (1.0 + x / 2.0) * Math.Exp(-x / 2.0);
            Numerics.AssertRelativeClose(expected, ChiSquared.SurvivalFunction(x, 4), 1e-9);
        }
    }

    [Fact]
    public void SurvivalFunction_OneDegreeOfFreedom_MatchesNormalTail()
    {
        // SF(1, 1) = P(|Z| > 1) = 0.3173105.
        Numerics.AssertRelativeClose(0.3173105, ChiSquared.SurvivalFunction(1.0, 1), 1e-4);
    }

    [Theory]
    // Standard 0.05 critical values: SF(crit, df) == 0.05.
    [InlineData(3.8415, 1)]
    [InlineData(5.9915, 2)]
    [InlineData(7.8147, 3)]
    [InlineData(9.4877, 4)]
    public void SurvivalFunction_AtCriticalValue_IsFivePercent(double critical, int df)
    {
        Numerics.AssertRelativeClose(0.05, ChiSquared.SurvivalFunction(critical, df), 1e-3);
    }

    [Fact]
    public void SurvivalFunction_IsMonotonicallyDecreasing()
    {
        var previous = 1.0;

        for (var x = 0.5; x <= 20; x += 0.5)
        {
            var current = ChiSquared.SurvivalFunction(x, 3);
            Assert.True(current <= previous, $"SF should be non-increasing; SF({x}) = {current} > {previous}.");
            previous = current;
        }
    }
}
