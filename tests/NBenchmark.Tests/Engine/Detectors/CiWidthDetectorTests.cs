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
            if (detector.Feed(i % 2 == 0 ? 10.0 : 200.0, stopAllowed: true))
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
            detector.Feed(s, stopAllowed: true);
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
                if (detector.Feed(100.0 + 5.0 * Math.Sin(i), stopAllowed: true))
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
            detector.Feed(100.0 + 10.0 * Math.Sin(i), stopAllowed: true);
        }

        Assert.True(detector.Resolved);
        Assert.Equal(SampleStopReason.MaxCeiling, detector.StopReason);

        // Evaluations at Count = 2, 4, 6, ..., 18 (nine cadence checks) plus the ceiling evaluation
        // at Count = 20. Count = 20 satisfies both the cadence and the ceiling, but the half-width is
        // computed once per sample, so it produces one entry, not two.
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
            detector.Feed(0.0, stopAllowed: true);
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
            if (detector.Feed(value, stopAllowed: true))
                return i;
        }

        throw new InvalidOperationException("Detector did not resolve.");
    }

    [Fact]
    public void StopAllowed_False_Blocks_CiTargetMet_But_Keeps_Accumulating()
    {
        // A tight, easily-converging stream that would stop on the CI target immediately. With the
        // outside floor withholding permission, the detector must keep going - this is how the
        // measurement time floor composes on top without the detector knowing about it.
        var options = AutoTuneOptions.Default with { MinSamples = 2, BatchSize = 2, MaxSamples = 500, CiTarget = 0.5 };
        var detector = new CiWidthDetector(0.95, options);

        for (var i = 0; i < 100; i++)
        {
            Assert.False(detector.Feed(100.0, stopAllowed: false));
        }

        Assert.False(detector.Resolved);
        Assert.Equal(100, detector.Count);

        // The half-width was still tracked while stopping was blocked, so the convergence trace does
        // not go dark during the floor.
        Assert.NotEmpty(detector.HalfWidthSeries);

        // Once permission arrives, the next *cadence* check resolves. Sample 101 is off-cadence, so the
        // stop rule is not evaluated there; sample 102 is.
        Assert.False(detector.Feed(100.0, stopAllowed: true));
        Assert.True(detector.Feed(100.0, stopAllowed: true));
        Assert.Equal(SampleStopReason.CiTargetMet, detector.StopReason);
    }

    [Fact]
    public void StopAllowed_False_Does_Not_Block_MaxCeiling()
    {
        // The ceiling must latch regardless of the floor, or a nano-scale body that can never
        // accumulate the required duration would spin forever.
        var options = AutoTuneOptions.Default with { MinSamples = 2, BatchSize = 2, MaxSamples = 10, CiTarget = 0.5 };
        var detector = new CiWidthDetector(0.95, options);

        var resolvedAt = 0;

        for (var i = 1; i <= 10; i++)
        {
            if (detector.Feed(100.0 + 10.0 * Math.Sin(i), stopAllowed: false))
            {
                resolvedAt = i;
                break;
            }
        }

        Assert.Equal(10, resolvedAt);
        Assert.Equal(SampleStopReason.MaxCeiling, detector.StopReason);
    }

    [Fact]
    public void CiTargetMet_Wins_Over_MaxCeiling_On_The_Boundary_Sample()
    {
        // When the final permitted sample both reaches MaxSamples and meets the target, the target was
        // genuinely met - reporting MaxCeiling would attach a "wider than requested" warning to a run
        // that satisfied the request.
        var options = AutoTuneOptions.Default with { MinSamples = 2, BatchSize = 10, MaxSamples = 10, CiTarget = 0.5 };
        var detector = new CiWidthDetector(0.95, options);

        for (var i = 1; i <= 10; i++)
        {
            detector.Feed(100.0, stopAllowed: true);
        }

        Assert.True(detector.Resolved);
        Assert.Equal(SampleStopReason.CiTargetMet, detector.StopReason);
    }

    [Fact]
    public void CoefficientOfVariation_Tracks_The_Welford_State()
    {
        var options = AutoTuneOptions.Default with { MinSamples = 2, BatchSize = 2, MaxSamples = 1_000, CiTarget = 0.0 };
        var detector = new CiWidthDetector(0.95, options);

        // Alternating 90/110 around a mean of 100 gives a sample StdDev of ~10.26 at n = 100.
        for (var i = 0; i < 100; i++)
        {
            detector.Feed(i % 2 == 0 ? 90.0 : 110.0, stopAllowed: true);
        }

        Assert.Equal(detector.StandardDeviation / detector.Mean, detector.CoefficientOfVariation, 12);
        Assert.InRange(detector.CoefficientOfVariation, 0.09, 0.11);
    }

    [Fact]
    public void CoefficientOfVariation_Is_NaN_Before_A_Positive_Mean()
    {
        var detector = new CiWidthDetector(0.95, AutoTuneOptions.Default);
        Assert.True(double.IsNaN(detector.CoefficientOfVariation));
    }

    /// <summary>
    ///     The stop rule evaluates the half-width on a cadence and stops at the first crossing -
    ///     optional stopping. Each evaluation is a "look" at the accumulating data, and a CI
    ///     computed at the nominal level at the stopping look no longer has its nominal coverage,
    ///     because the loop stopped precisely when the interval happened to be narrow. The post-hoc
    ///     <see cref="CiWidthDetector.ReportedHalfWidth" /> recomputes the interval on the final
    ///     array with a Bonferroni-widened critical value over the actual number of looks
    ///     (<c>t_eff = t_{1 - α/looks, df}</c>), so it is at least as wide as the naive
    ///     single-look interval. A deterministic moderate-spread stream that resolves after several
    ///     cadence checks exercises the multi-look case.
    /// </summary>
    [Fact]
    public void ReportedHalfWidth_BonferroniWidensBeyondNaive_AfterMultipleLooks()
    {
        var options = AutoTuneOptions.Default with
        {
            MinSamples = 10,
            BatchSize = 2,
            MaxSamples = 2_000,
            CiTarget = 0.01,
        };
        var detector = new CiWidthDetector(0.95, options);

        var samples = new List<double>();
        for (var i = 1; i <= 2_000; i++)
        {
            var s = 100.0 + 5.0 * Math.Sin(i);
            samples.Add(s);
            if (detector.Feed(s, stopAllowed: true))
                break;
        }

        Assert.True(detector.Resolved);
        Assert.True(detector.LookCount >= 2, $"expected multiple looks, got {detector.LookCount}");

        var arr = samples.ToArray();
        var n = arr.Length;
        var mean = arr.Average();
        var stddev = Math.Sqrt(arr.Sum(d => (d - mean) * (d - mean)) / (n - 1));
        var naive = StudentT.CriticalValue(0.95, n - 1) * stddev / Math.Sqrt(n);

        var postHoc = detector.ReportedHalfWidth(arr);

        Assert.True(double.IsFinite(postHoc));
        // Bonferroni over >= 2 looks widens the critical value, so the post-hoc interval is at
        // least the naive one - and strictly wider here, not merely tied.
        Assert.True(postHoc >= naive, $"post-hoc {postHoc} should be >= naive {naive}");
        Assert.True(postHoc > naive * 1.05, $"post-hoc {postHoc} should widen beyond naive {naive}");
    }

    /// <summary>
    ///     With a single look there is no optional stopping - the loop stopped on the first
    ///     evaluation, so no repeated-looks bias inflates the family-wise error rate. The post-hoc
    ///     interval therefore equals the naive nominal one (the Bonferroni factor over one look is
    ///     the identity), confirming the correction is opt-in to the multi-look case rather than
    ///     always widening.
    /// </summary>
    [Fact]
    public void ReportedHalfWidth_EqualsNaive_WhenSingleLook()
    {
        // A loose target and the first two samples spread apart just enough to have non-zero
        // variance, but whose relative half-width still falls under the target, resolves on the
        // very first evaluation (n = 2) - one look.
        var options = AutoTuneOptions.Default with
        {
            MinSamples = 2,
            BatchSize = 1,
            MaxSamples = 2_000,
            CiTarget = 2.0,
        };
        var detector = new CiWidthDetector(0.95, options);

        var samples = new List<double> { 90.0, 110.0 };
        // The first sample (n = 1) is below MinSamples and cannot evaluate; the second (n = 2)
        // is the first cadence check and resolves immediately - one look.
        Assert.False(detector.Feed(90.0, stopAllowed: true));
        Assert.True(detector.Feed(110.0, stopAllowed: true));
        Assert.Equal(1, detector.LookCount);

        var arr = samples.ToArray();
        var n = arr.Length;
        var mean = arr.Average();
        var stddev = Math.Sqrt(arr.Sum(d => (d - mean) * (d - mean)) / (n - 1));
        var naive = StudentT.CriticalValue(0.95, n - 1) * stddev / Math.Sqrt(n);

        var postHoc = detector.ReportedHalfWidth(arr);

        Assert.Equal(naive, postHoc, 9);
    }
}
