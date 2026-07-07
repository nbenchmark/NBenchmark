using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class RegressionToleranceTests
{
    [Fact]
    public void Evaluate_MultiplierOne_DoesNotRelax_EffectiveThresholdEqualsConfigured()
    {
        var verdict = RegressionTolerance.Evaluate(measuredValue: 100, configuredThreshold: 200, toleranceMultiplier: 1.0);

        Assert.False(verdict.Relaxed);
        Assert.Equal(200d, verdict.EffectiveThreshold, 12);
        Assert.False(verdict.ExceedsThreshold);
        Assert.Equal(0d, verdict.Excess, 12);
    }

    [Fact]
    public void Evaluate_MeasuredValueAboveConfigured_WithoutRelaxation_ExceedsThreshold()
    {
        var verdict = RegressionTolerance.Evaluate(measuredValue: 250, configuredThreshold: 200, toleranceMultiplier: 1.0);

        Assert.True(verdict.ExceedsThreshold);
        Assert.Equal(50d, verdict.Excess, 12);
        Assert.False(verdict.Relaxed);
    }

    [Fact]
    public void Evaluate_MeasuredValueAboveConfigured_ButWithinRelaxedThreshold_DoesNotExceed()
    {
        var verdict = RegressionTolerance.Evaluate(measuredValue: 250, configuredThreshold: 200, toleranceMultiplier: 2.0);

        Assert.True(verdict.Relaxed);
        Assert.Equal(400d, verdict.EffectiveThreshold, 12);
        Assert.False(verdict.ExceedsThreshold);
        Assert.Equal(0d, verdict.Excess, 12);
    }

    [Fact]
    public void Evaluate_MeasuredValueAboveRelaxedThreshold_ExceedsAndReportsExcessOverEffective()
    {
        var verdict = RegressionTolerance.Evaluate(measuredValue: 500, configuredThreshold: 200, toleranceMultiplier: 2.0);

        Assert.True(verdict.Relaxed);
        Assert.True(verdict.ExceedsThreshold);
        Assert.Equal(100d, verdict.Excess, 12);
    }

    [Fact]
    public void Evaluate_MeasuredValueExactlyAtEffectiveThreshold_DoesNotExceed_StrictGreaterThan()
    {
        // The contract is `measuredValue > effectiveThreshold`, so an exact
        // match at the threshold boundary must not flag a violation.
        var verdict = RegressionTolerance.Evaluate(measuredValue: 400, configuredThreshold: 200, toleranceMultiplier: 2.0);

        Assert.False(verdict.ExceedsThreshold);
        Assert.Equal(0d, verdict.Excess, 12);
    }

    [Fact]
    public void Evaluate_MultiplierBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RegressionTolerance.Evaluate(measuredValue: 100, configuredThreshold: 200, toleranceMultiplier: 0.99));
    }

    [Fact]
    public void Evaluate_MultiplierExactlyOne_IsAllowed_NoRelaxation()
    {
        // Boundary: multiplier == 1.0 is the no-relaxation case; the guard is
        // `< 1.0`, so 1.0 itself must be accepted.
        var verdict = RegressionTolerance.Evaluate(measuredValue: 100, configuredThreshold: 200, toleranceMultiplier: 1.0);

        Assert.False(verdict.Relaxed);
        Assert.Equal(200d, verdict.EffectiveThreshold, 12);
    }

    [Fact]
    public void Evaluate_VerdictCarriesMeasuredAndConfiguredValues()
    {
        var verdict = RegressionTolerance.Evaluate(measuredValue: 150, configuredThreshold: 100, toleranceMultiplier: 1.5);

        Assert.Equal(150d, verdict.MeasuredValue, 12);
        Assert.Equal(100d, verdict.ConfiguredThreshold, 12);
        Assert.Equal(1.5, verdict.ToleranceMultiplier, 12);
    }

    [Fact]
    public void NeedsRelaxation_SharedRunner_ReturnsTrueRegardlessOfJitter()
    {
        var result = MakeResult(jitterMetric: null);

        Assert.True(RegressionTolerance.NeedsRelaxation(result, isSharedRunner: true, jitterAutoSwitchThreshold: 0.10));
    }

    [Fact]
    public void NeedsRelaxation_NotSharedRunner_LowJitter_ReturnsFalse()
    {
        var result = MakeResult(jitterMetric: 0.05);

        Assert.False(RegressionTolerance.NeedsRelaxation(result, isSharedRunner: false, jitterAutoSwitchThreshold: 0.10));
    }

    [Fact]
    public void NeedsRelaxation_NotSharedRunner_JitterAboveThreshold_ReturnsTrue()
    {
        var result = MakeResult(jitterMetric: 0.20);

        Assert.True(RegressionTolerance.NeedsRelaxation(result, isSharedRunner: false, jitterAutoSwitchThreshold: 0.10));
    }

    [Fact]
    public void NeedsRelaxation_JitterExactlyAtThreshold_ReturnsFalse_StrictGreaterThan()
    {
        // The contract is `jitter > threshold`, so an exact match at the
        // threshold boundary must not trigger relaxation.
        var result = MakeResult(jitterMetric: 0.10);

        Assert.False(RegressionTolerance.NeedsRelaxation(result, isSharedRunner: false, jitterAutoSwitchThreshold: 0.10));
    }

    [Fact]
    public void NeedsRelaxation_NullJitterMetric_NotSharedRunner_ReturnsFalse()
    {
        var result = MakeResult(jitterMetric: null);

        Assert.False(RegressionTolerance.NeedsRelaxation(result, isSharedRunner: false, jitterAutoSwitchThreshold: 0.10));
    }

    private static BenchmarkResult MakeResult(double? jitterMetric)
    {
        AutoTuneDiagnostic? autoTune = jitterMetric.HasValue
            ? new AutoTuneDiagnostic
            {
                ResolvedWarmup = 1,
                ResolvedSamples = 1,
                OpsPerSample = 1,
                TotalBodyInvocations = 1,
                WarmupStop = WarmupStopReason.Settled,
                SampleStop = SampleStopReason.CiTargetMet,
                AchievedRelativeCiWidth = 0.01,
                TuningWallClock = TimeSpan.Zero,
                JitterMetric = jitterMetric,
            }
            : null;

        return new BenchmarkResult
        {
            Name = "test",
            Mean = 100,
            Median = 100,
            Min = 90,
            Max = 110,
            StandardDeviation = 5,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 1,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
            AutoTune = autoTune,
        };
    }
}