using NBenchmark.Engine.Detectors;
using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class CiWidthDetectorTests
{
    [Fact]
    public void TightDistribution_ResolvesAtCiTarget()
    {
        // MinSamples 1 / BatchSize 1 evaluate the stop rule from the earliest valid sample,
        // isolating the CI math. A constant stream has zero variance, so the half-width is 0
        // and the target is met as soon as the variance is defined (n = 2).
        var options = AutoTuneOptions.Default with { MinSamples = 1, BatchSize = 1, CiTarget = 0.025 };
        var detector = new CiWidthDetector(0.95, options);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(SampleStopReason.CiTargetMet, detector.StopReason);
        Assert.Equal(2, resolvedAt);
        Assert.Equal(0.0, detector.AchievedRelativeHalfWidth);
    }

    [Fact]
    public void WideDistribution_StopsAtCeiling()
    {
        var options = AutoTuneOptions.Default with { MinSamples = 2, MaxSamples = 20, BatchSize = 2, CiTarget = 0.001 };
        var detector = new CiWidthDetector(0.95, options);

        var resolvedAt = 0;

        for (var i = 1; i <= 1_000; i++)
        {
            // A wildly bimodal stream cannot reach a 0.1% half-width within the ceiling.
            if (detector.Feed(i % 2 == 0 ? 10.0 : 200.0))
            {
                resolvedAt = i;
                break;
            }
        }

        Assert.True(detector.Resolved);
        Assert.Equal(SampleStopReason.MaxCeiling, detector.StopReason);
        Assert.Equal(20, resolvedAt);
    }

    [Fact]
    public void WelfordMatchesStatsSummary()
    {
        var samples = Enumerable.Range(0, 50).Select(i => 100.0 + 7.0 * Math.Sin(i)).ToArray();

        // Configure so the detector never resolves mid-feed: the floor sits past the data and
        // the target is unreachable, leaving the accumulator to consume every sample.
        var options = AutoTuneOptions.Default with { MinSamples = samples.Length + 1, MaxSamples = samples.Length + 1, CiTarget = 0.0 };
        var detector = new CiWidthDetector(0.95, options);

        foreach (var s in samples)
        {
            detector.Feed(s);
        }

        var expected = StatsSummary.Compute(samples);

        Assert.Equal(expected.Mean, detector.Mean, 9);
        Assert.Equal(expected.StandardDeviation, detector.StandardDeviation, 9);
    }

    [Theory]
    [InlineData(0.90)]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void ConfidenceLevelAffectsStop(double confidenceLevel)
    {
        var options = AutoTuneOptions.Default with { MinSamples = 2, BatchSize = 1, MaxSamples = 100_000, CiTarget = 0.02 };

        var counts = new Dictionary<double, long>();

        foreach (var cl in new[] { 0.90, 0.95, 0.99 })
        {
            var detector = new CiWidthDetector(cl, options);

            for (var i = 1; i <= 100_000; i++)
            {
                // Deterministic moderate-spread stream (CV ~ 3.5%).
                if (detector.Feed(100.0 + 5.0 * Math.Sin(i)))
                    break;
            }

            Assert.True(detector.Resolved);
            counts[cl] = detector.Count;
        }

        // A higher confidence level needs a wider interval, hence at least as many samples.
        Assert.True(counts[0.90] <= counts[0.95]);
        Assert.True(counts[0.95] <= counts[0.99]);
        Assert.True(counts.ContainsKey(confidenceLevel));
    }

    [Fact]
    public void HalfWidthSeries_RecordsAtCadenceAndMatchesAchievedHalfWidth()
    {
        // Cadence 2 with MinSamples 2 evaluates the stop rule every other sample once past the
        // floor. With an unreachable CiTarget the loop runs to the MaxSamples ceiling, so an
        // entry is appended at every cadence check plus the final MaxCeiling evaluation.
        var options = AutoTuneOptions.Default with
        {
            MinSamples = 2,
            BatchSize = 2,
            MaxSamples = 20,
            CiTarget = 0.001, // unreachable for a varied stream, so the loop hits MaxCeiling
        };
        var detector = new CiWidthDetector(0.95, options);

        // Deterministic stream with non-zero variance. ComputeHalfWidth succeeds once n >= 2
        // and the mean is positive, so every cadence check appends an entry.
        for (var i = 1; i <= 20; i++)
        {
            detector.Feed(100.0 + 10.0 * Math.Sin(i));
        }

        Assert.True(detector.Resolved);
        Assert.Equal(SampleStopReason.MaxCeiling, detector.StopReason);

        // Evaluations at Count = 2, 4, 6, ..., 18 (nine cadence checks) plus the MaxCeiling
        // evaluation at Count = 20. The MaxCeiling branch fires before the cadence branch when
        // Count == _maxSamples, so Count = 20 produces one entry, not two.
        Assert.Equal(10, detector.HalfWidthSeries.Count);

        // The final series entry must match the last AchievedRelativeHalfWidth the detector
        // reported: the series records the same value at each append.
        Assert.Equal(detector.AchievedRelativeHalfWidth, detector.HalfWidthSeries[^1], 12);

        // Every entry is finite and non-negative (it is |t * SE / mean|).
        foreach (var value in detector.HalfWidthSeries)
        {
            Assert.True(double.IsFinite(value));
            Assert.True(value >= 0.0);
        }
    }

    [Fact]
    public void HalfWidthSeries_EmptyWhenComputeHalfWidthNeverSucceeds()
    {
        // ComputeHalfWidth returns false (and does not append) when Count < 2 or Mean <= 0.
        // Feeding only zeros keeps Mean = 0, so no evaluation ever appends. The detector still
        // resolves at the MaxCeiling because the MaxCeiling branch calls ComputeHalfWidth but
        // ignores its return value.
        var options = AutoTuneOptions.Default with
        {
            MinSamples = 2,
            MaxSamples = 4,
            BatchSize = 1,
            CiTarget = 0.0,
        };
        var detector = new CiWidthDetector(0.95, options);

        for (var i = 0; i < 4; i++)
        {
            detector.Feed(0.0);
        }

        Assert.True(detector.Resolved);
        Assert.Equal(SampleStopReason.MaxCeiling, detector.StopReason);
        Assert.Empty(detector.HalfWidthSeries);
    }

    [Fact]
    public void HalfWidthSeries_EmptyBeforeFirstEvaluation()
    {
        // Construct but never feed: the series is empty by construction.
        var options = AutoTuneOptions.Default with { MinSamples = 5, MaxSamples = 100, BatchSize = 1 };
        var detector = new CiWidthDetector(0.95, options);

        Assert.Empty(detector.HalfWidthSeries);
    }

    private static int FeedConstantUntilResolved(CiWidthDetector detector, double value, int cap)
    {
        for (var i = 1; i <= cap; i++)
        {
            if (detector.Feed(value))
                return i;
        }

        throw new InvalidOperationException("Detector did not resolve.");
    }
}
