using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class SignificanceStrategyTests
{
    [Fact]
    public void Default_TwoGroups_UsesPairwiseMannWhitney()
    {
        var context = new SignificanceContext
        {
            Groups =
            [
                new SampleGroup("baseline", Cluster(10), true),
                new SampleGroup("candidate", Cluster(100), false),
            ],
            BaselineIndex = 0,
            SignificanceLevel = 0.05,
        };

        var report = DefaultSignificanceTest.Instance.Analyze(context);

        Assert.Null(report.Omnibus);
        var comparison = Assert.Single(report.Pairwise);
        Assert.Equal("candidate", comparison.Name);
        Assert.Equal(SignificanceVerdict.Significant, comparison.Verdict);
        Assert.NotNull(comparison.PValue);
    }

    [Fact]
    public void Default_ThreeGroups_RunsOmnibusAndPostHocPairwise()
    {
        var context = new SignificanceContext
        {
            Groups =
            [
                new SampleGroup("a", Cluster(10), true),
                new SampleGroup("b", Cluster(50), false),
                new SampleGroup("c", Cluster(100), false),
            ],
            BaselineIndex = 0,
            SignificanceLevel = 0.05,
        };

        var report = DefaultSignificanceTest.Instance.Analyze(context);

        Assert.NotNull(report.Omnibus);
        Assert.Equal("Kruskal-Wallis", report.Omnibus!.TestName);
        Assert.Equal(3, report.Omnibus.GroupCount);
        Assert.Equal(2, report.Omnibus.DegreesOfFreedom);
        Assert.Equal(SignificanceVerdict.Significant, report.Omnibus.Verdict);

        // Post-hoc pairwise entries should be present (one per candidate).
        Assert.Equal(2, report.Pairwise.Count);
        Assert.Contains(report.Pairwise, p => p.Name == "b");
        Assert.Contains(report.Pairwise, p => p.Name == "c");
        Assert.All(report.Pairwise, p => Assert.Equal(SignificanceVerdict.Significant, p.Verdict));
    }

    /// <summary>
    ///     In the three-or-more group path the verdict is decided from the Holm-Bonferroni
    ///     <i>adjusted</i> p, but the p stored on the <see cref="PairwiseComparison" /> (and so
    ///     written onto <see cref="BenchmarkResult.PValue" />) was the raw p. A row could then show
    ///     a p below the significance level beside a NotSignificant verdict, or simply a p the
    ///     verdict was never based on. The reported p must be the one the verdict was decided from.
    /// </summary>
    [Fact]
    public void Default_ThreeGroups_PostHoc_ReportsHolmAdjustedPValue()
    {
        var baseline = Cluster(10, 0);
        var candidates = new[]
        {
            ("b1", Cluster(10, 0.2)),
            ("b2", Cluster(10, 0.4)),
            ("b3", Cluster(10, 0.6)),
            ("b4", Cluster(10, 0.8)),
        };

        // Recompute the expected raw and Holm-adjusted p from the same samples the strategy
        // sees, so the assertion is independent of the exact Mann-Whitney values.
        var rawP = candidates.Select(c => MannWhitneyU.Test(baseline, c.Item2).PValue).ToList();
        var adjustedP = MultipleComparisons.HolmBonferroni(rawP);

        var context = new SignificanceContext
        {
            Groups = [new("a", baseline, true), .. candidates.Select(c => new SampleGroup(c.Item1, c.Item2, false))],
            BaselineIndex = 0,
            SignificanceLevel = 0.05,
        };

        var report = DefaultSignificanceTest.Instance.Analyze(context);

        // Omnibus must be significant, or the post-hoc path (the one with the bug) never runs.
        Assert.Equal(SignificanceVerdict.Significant, report.Omnibus!.Verdict);

        // The reported p must be the adjusted p the verdict was decided from, not the raw p.
        for (var i = 0; i < candidates.Length; i++)
        {
            var comparison = report.Pairwise.Single(p => p.Name == candidates[i].Item1);
            Assert.NotNull(comparison.PValue);
            Assert.Equal(adjustedP[i], comparison.PValue!.Value, 12);
        }

        // The case must be meaningful: at least one candidate's adjusted p differs from its raw
        // p (Holm inflated it), and the report carries the inflated value rather than the raw.
        var inflated = Enumerable.Range(0, candidates.Length)
            .First(i => adjustedP[i] != rawP[i]);
        Assert.NotEqual(rawP[inflated], report.Pairwise.Single(p => p.Name == candidates[inflated].Item1).PValue);
    }

    [Fact]
    public void Default_ThreeGroups_OmnibusNotSignificant_SkipsPostHoc()
    {
        var context = new SignificanceContext
        {
            Groups =
            [
                new SampleGroup("a", Cluster(10), true),
                new SampleGroup("b", Cluster(10), false),
                new SampleGroup("c", Cluster(10), false),
            ],
            BaselineIndex = 0,
            SignificanceLevel = 0.05,
        };

        var report = DefaultSignificanceTest.Instance.Analyze(context);

        Assert.NotNull(report.Omnibus);
        Assert.Equal(SignificanceVerdict.NotSignificant, report.Omnibus!.Verdict);
        Assert.Empty(report.Pairwise);
    }

    [Fact]
    public void ComputeSignificance_ThreeGroupsOmnibusSignificant_SetsPerRowVerdictsFromPostHoc()
    {
        var results = new List<BenchmarkResult>
        {
            Result("a", true),
            Result("b"),
            Result("c"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["a"] = Cluster(10),
            ["b"] = Cluster(50),
            ["c"] = Cluster(100),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.All(results, r => Assert.NotNull(r.Omnibus));
        Assert.Equal("Kruskal-Wallis", results[0].Omnibus!.TestName);

        // Post-hoc pairwise verdicts should be set on each candidate.
        Assert.Equal(SignificanceVerdict.NotTested, results[0].SignificanceVerdict); // baseline
        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
        Assert.Equal(SignificanceVerdict.Significant, results[2].SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_ThreeGroupsOmnibusNotSignificant_LeavesVerdictsUntested()
    {
        var results = new List<BenchmarkResult>
        {
            Result("a", true),
            Result("b"),
            Result("c"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["a"] = Cluster(10),
            ["b"] = Cluster(10),
            ["c"] = Cluster(10),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.All(results, r => Assert.NotNull(r.Omnibus));
        Assert.Equal(SignificanceVerdict.NotSignificant, results[0].Omnibus!.Verdict);

        // Post-hoc skipped; per-row verdicts stay NotTested.
        Assert.All(results, r => Assert.Equal(SignificanceVerdict.NotTested, r.SignificanceVerdict));
    }

    [Fact]
    public void Default_ThreeGroups_CandidateWithTooFewSamples_ReportsNullPValueAndNotTested()
    {
        var context = new SignificanceContext
        {
            Groups =
            [
                new SampleGroup("a", Cluster(10), true),
                new SampleGroup("b", Cluster(50), false),
                new SampleGroup("c", [1.0], false), // too few for Mann-Whitney U
            ],
            BaselineIndex = 0,
            SignificanceLevel = 0.05,
        };

        var report = DefaultSignificanceTest.Instance.Analyze(context);

        Assert.NotNull(report.Omnibus);
        Assert.Equal(SignificanceVerdict.Significant, report.Omnibus!.Verdict);

        var c = report.Pairwise.Single(p => p.Name == "c");
        Assert.Null(c.PValue);
        Assert.Equal(SignificanceVerdict.NotTested, c.Verdict);
    }

    [Fact]
    public void ComputeSignificance_TwoGroups_SetsPairwiseVerdictAndNoOmnibus()
    {
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("candidate"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["candidate"] = Cluster(100),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].Omnibus);
        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
        Assert.NotNull(results[1].PValue);
    }

    [Fact]
    public void ComputeSignificance_ImplicitBaseline_UsesLaunchMedianWhenPresent()
    {
        var results = new List<BenchmarkResult>
        {
            Result("a") with
            {
                Median = 10,
                LaunchStatistics = new LaunchStatistics
                {
                    LaunchCount = 3,
                    LaunchMean = 30,
                    LaunchStandardDeviation = 2,
                    LaunchMedian = 30,
                },
            },
            Result("b") with
            {
                Median = 12,
                LaunchStatistics = new LaunchStatistics
                {
                    LaunchCount = 3,
                    LaunchMean = 20,
                    LaunchStandardDeviation = 2,
                    LaunchMedian = 20,
                },
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["a"] = Cluster(30),
            ["b"] = Cluster(10),
        };

        Significance.ComputeSignificance(results, rawSamples);

        // Launch medians make "b" the implicit baseline (faster typical launch), so
        // "a" is tested as the candidate.
        Assert.Equal(SignificanceVerdict.NotTested, results.Single(r => r.Name == "b").SignificanceVerdict);
        Assert.Equal(SignificanceVerdict.Significant, results.Single(r => r.Name == "a").SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_HonorsCustomSignificanceTest()
    {
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("candidate"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["candidate"] = Cluster(11),
        };

        var custom = new FixedSignificanceTest("candidate", 0.001);

        Significance.ComputeSignificance(results, rawSamples, custom);

        Assert.Equal(0.001, results[1].PValue);
        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_WarnsAndExcludes_CandidateWithMissingSamples()
    {
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("present"),
            Result("missing"),
        };

        // "missing" has no entry, so it should be excluded with a warning while the
        // remaining two benchmarks are still compared.
        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["present"] = Cluster(100),
        };

        Significance.ComputeSignificance(results, rawSamples);

        var missing = results.Single(r => r.Name == "missing");
        Assert.Contains(missing.Warnings, w => w.Contains("missing") && w.Contains("excluded"));

        // The two benchmarks that did have samples were still compared.
        Assert.Empty(results.Single(r => r.Name == "present").Warnings);
    }

    [Fact]
    public void ComputeSignificance_WarnsOnBaseline_WhenSignificanceCannotRun()
    {
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("candidate"),
        };

        // Only the candidate has samples, so fewer than two groups remain and significance
        // is skipped - which must be surfaced rather than dropped silently.
        var rawSamples = new Dictionary<string, double[]>
        {
            ["candidate"] = Cluster(100),
        };

        Significance.ComputeSignificance(results, rawSamples);

        var baseline = results.Single(r => r.Name == "baseline");
        Assert.Contains(baseline.Warnings, w => w.Contains("skipped"));
        Assert.All(results, r => Assert.Equal(SignificanceVerdict.NotTested, r.SignificanceVerdict));
    }

    [Fact]
    public void MeasurementOptions_ResolveSignificanceTest_PrefersCustomTest()
    {
        var custom = new FixedSignificanceTest("x", 0.5);

        Assert.IsType<DefaultSignificanceTest>(MeasurementOptions.Default.ResolveSignificanceTest());
        Assert.Same(custom, (MeasurementOptions.Default with { SignificanceTest = () => custom }).ResolveSignificanceTest());
    }

    [Fact]
    public void MannWhitneyStrategy_ReportsNotTested_ForTooFewSamples()
    {
        var context = new SignificanceContext
        {
            Groups =
            [
                new SampleGroup("baseline", [1], true),
                new SampleGroup("candidate", [2], false),
            ],
            BaselineIndex = 0,
            SignificanceLevel = 0.05,
        };

        var report = MannWhitneyUSignificanceTest.Instance.Analyze(context);

        var comparison = Assert.Single(report.Pairwise);
        Assert.Equal(SignificanceVerdict.NotTested, comparison.Verdict);
        Assert.Null(comparison.PValue);
    }

    [Fact]
    public void ComputeSignificance_MinimumPracticalEffect_DowngradesVerdictAndForcesNegligibleMagnitude()
    {
        // Construct a synthetic case where the engine sees a Significant
        // verdict with |CliffsDelta| below the threshold. With the threshold
        // set above the observed |delta|, the verdict must be downgraded to
        // NotSignificant and the Magnitude label forced to Negligible.
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("candidate"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["candidate"] = Cluster(10),
        };

        // A custom test reports Significant with |delta| = 0.1, which the
        // engine must downgrade to NotSignificant because 0.1 < 0.5.
        var custom = new DeltaReportingSignificanceTest("candidate", 0.1, 0.001);

        Significance.ComputeSignificance(
            results,
            rawSamples,
            custom,
            0.05,
            0.5);

        var candidateResult = results.Single(r => r.Name == "candidate");
        Assert.Equal(SignificanceVerdict.NotSignificant, candidateResult.SignificanceVerdict);
        Assert.Equal("neg", candidateResult.Effect?.Magnitude);

        // The downgrade is recorded as a discoverable warning (not silently swallowed).
        Assert.Contains(
            candidateResult.Warnings,
            w => w.Contains("practically negligible") && w.Contains("--min-practical-effect 0"));
    }

    [Fact]
    public void ComputeSignificance_MinimumPracticalEffect_NullIsNoOp()
    {
        // With a null threshold, the existing p-value-only Sig semantics are
        // preserved: a Significant verdict stays Significant and Magnitude
        // stays at whatever the test reported.
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("candidate"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["candidate"] = Cluster(10),
        };

        var custom = new DeltaReportingSignificanceTest("candidate", 0.1, 0.001);

        Significance.ComputeSignificance(
            results,
            rawSamples,
            custom);

        var candidateResult = results.Single(r => r.Name == "candidate");
        Assert.Equal(SignificanceVerdict.Significant, candidateResult.SignificanceVerdict);
        Assert.Equal("neg", candidateResult.Effect?.Magnitude);
    }

    [Fact]
    public void ComputeSignificance_MinimumPracticalEffect_DeltaAboveThreshold_KeepsVerdict()
    {
        // Sanity check: when |delta| exceeds the threshold, the engine does
        // not touch the verdict or magnitude.
        var results = new List<BenchmarkResult>
        {
            Result("baseline", true),
            Result("candidate"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["candidate"] = Cluster(10),
        };

        var custom = new DeltaReportingSignificanceTest("candidate", 0.8, 0.001);

        Significance.ComputeSignificance(
            results,
            rawSamples,
            custom,
            0.05,
            0.5);

        var candidate = results.Single(r => r.Name == "candidate");
        Assert.Equal(SignificanceVerdict.Significant, candidate.SignificanceVerdict);
        Assert.Equal("large", candidate.Effect?.Magnitude);
    }

    /// <summary>
    ///     A statistically significant result with a tiny absolute shift but near-zero spread passes
    ///     the practical-effect gate (|Cliff's delta| is ~1) yet describes a change no one would act
    ///     on: 0.3 ns on a 100 ns baseline is 0.3% relative. The minimum relative-shift gate
    ///     downgrades that to NotSignificant, so a ✓ means "real, at least a small effect, and at
    ///     least a 1% shift" rather than flagging sub-percent noise that happened to be consistent.
    /// </summary>
    [Fact]
    public void ComputeSignificance_MinimumRelativeShift_DowngradesSubPercentShift()
    {
        var rng = new Random(1);
        double[] baselineSamples = Enumerable.Range(0, 200).Select(_ => 100.0 + rng.NextDouble() * 0.01).ToArray();
        double[] candidateSamples = Enumerable.Range(0, 200).Select(_ => 100.3 + rng.NextDouble() * 0.01).ToArray();

        var results = new List<BenchmarkResult>
        {
            Result("baseline", true) with { Mean = 100, Median = 100 },
            Result("candidate") with { Mean = 100.3, Median = 100.3 },
        };
        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        // Practical-effect gate disabled (0) so the relative-shift gate is the only one acting,
        // and a 1% minimum relative shift: 0.3% falls below it.
        Significance.ComputeSignificance(results, rawSamples, 0.05,
            minimumPracticalEffect: 0, minimumRelativeShift: 0.01);

        var candidate = results.Single(r => r.Name == "candidate");

        // Without the gate the U test rejects and |delta| ~ 1, so this would be Significant; the
        // relative-shift gate must downgrade it.
        Assert.Equal(SignificanceVerdict.NotSignificant, candidate.SignificanceVerdict);
        Assert.Contains(candidate.Warnings, w => w.Contains("relative"));
    }

    /// <summary>
    ///     With the gate disabled (<c>MinimumRelativeShift = 0</c>) the same sub-percent shift keeps
    ///     its Significant verdict, so the gate is opt-out rather than always-on.
    /// </summary>
    [Fact]
    public void ComputeSignificance_MinimumRelativeShift_ZeroKeepsVerdict()
    {
        var rng = new Random(1);
        double[] baselineSamples = Enumerable.Range(0, 200).Select(_ => 100.0 + rng.NextDouble() * 0.01).ToArray();
        double[] candidateSamples = Enumerable.Range(0, 200).Select(_ => 100.3 + rng.NextDouble() * 0.01).ToArray();

        var results = new List<BenchmarkResult>
        {
            Result("baseline", true) with { Mean = 100, Median = 100 },
            Result("candidate") with { Mean = 100.3, Median = 100.3 },
        };
        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        Significance.ComputeSignificance(results, rawSamples, 0.05,
            minimumPracticalEffect: 0, minimumRelativeShift: 0);

        var candidate = results.Single(r => r.Name == "candidate");
        Assert.Equal(SignificanceVerdict.Significant, candidate.SignificanceVerdict);
    }

    /// <summary>
    ///     Cross-launch pooling is a mixture, not IID: <c>PoolRawSamplesByName</c> concatenates every
    ///     launch's raw samples before the significance test, so a between-launch location offset is
    ///     detected at full pooled n regardless of whether the code differs. Here the candidate and
    ///     baseline are the same code, but the candidate's three launches drew medians of 100, 130 and
    ///     160 ns while the baseline's stayed at 100 - pure process variance, not a code change. The
    ///     pooled Mann-Whitney U sees the candidate shifted up and calls it Significant; the
    ///     launch-blocked paired-t on the three per-launch medians (reusing <c>LogRatio.Estimate</c>)
    ///     cannot rule out a ratio of 1.0 across only three noisy pairs, so it correctly returns
    ///     NotSignificant and names the pooled verdict as reproducibility-only rather than a code
    ///     change. Both gates are disabled here so the pooled verdict is the raw U verdict.
    /// </summary>
    [Fact]
    public void ComputeSignificance_LaunchBlockedVerdict_FlagsReproducibilityOnlyDifference()
    {
        var baselineSamples = Mixture(100, 100, 100);
        // Candidate pooled samples are the same three launch centres the per-launch medians report,
        // so the pooled U test sees the candidate shifted up.
        var candidateSamples = Mixture(100, 130, 160);

        var results = new List<BenchmarkResult>
        {
            Result("baseline", true) with
            {
                Mean = 100, Median = 100,
                LaunchStatistics = Launches(100, 100, 100),
            },
            Result("candidate") with
            {
                Mean = 130, Median = 130,
                LaunchStatistics = Launches(100, 130, 160),
            },
        };
        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        Significance.ComputeSignificance(results, rawSamples, 0.05,
            minimumPracticalEffect: 0, minimumRelativeShift: 0);

        var candidate = results.Single(r => r.Name == "candidate");

        // Pooled: the candidate is shifted up, so the U test rejects.
        Assert.Equal(SignificanceVerdict.Significant, candidate.SignificanceVerdict);

        // Launch-blocked: three noisy per-launch pairs (ratios 1.00, 1.30, 1.60) give a wide
        // interval that spans 1.0, so the launches do not separate the two.
        Assert.Equal(SignificanceVerdict.NotSignificant, candidate.LaunchBlockedVerdict);

        // The discrepancy is named as a reproducibility-only difference, not silently read as a
        // code change. This is the consequence W-41 makes legible below the ProcessVarianceRatio>4
        // threshold that DescribeReproducibility alone would miss.
        Assert.Contains(candidate.Warnings, w => w.Contains("launch") && w.Contains("reproducibility"));
    }

    /// <summary>
    ///     When the candidate is genuinely ~1.3x slower in every launch with little launch-to-launch
    ///     spread, the per-launch paired interval excludes 1.0 and the launch-blocked verdict agrees
    ///     with the pooled one. No reproducibility-only warning is raised, because the difference
    ///     reproduces across launches - it is a real code change, not a process draw.
    /// </summary>
    [Fact]
    public void ComputeSignificance_LaunchBlockedVerdict_ConfirmsRealCodeChange()
    {
        var baselineSamples = Mixture(100, 100, 100);
        var candidateSamples = Mixture(130, 131, 129);

        var results = new List<BenchmarkResult>
        {
            Result("baseline", true) with
            {
                Mean = 100, Median = 100,
                LaunchStatistics = Launches(100, 100, 100),
            },
            Result("candidate") with
            {
                Mean = 130, Median = 130,
                LaunchStatistics = Launches(130, 131, 129),
            },
        };
        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        Significance.ComputeSignificance(results, rawSamples, 0.05,
            minimumPracticalEffect: 0, minimumRelativeShift: 0);

        var candidate = results.Single(r => r.Name == "candidate");

        Assert.Equal(SignificanceVerdict.Significant, candidate.SignificanceVerdict);
        Assert.Equal(SignificanceVerdict.Significant, candidate.LaunchBlockedVerdict);
        Assert.DoesNotContain(candidate.Warnings, w => w.Contains("reproducibility"));
    }

    /// <summary>
    ///     With a single launch there is only one per-launch pair, and one pair is a ratio rather than
    ///     an estimate of one - it carries no information about how the ratio would move on a re-run.
    ///     The launch-blocked verdict stays <see cref="SignificanceVerdict.NotTested" /> and raises no
    ///     reproducibility warning, because there is no between-launch spread to describe.
    /// </summary>
    [Fact]
    public void ComputeSignificance_LaunchBlockedVerdict_NotTested_WhenSingleLaunch()
    {
        var baselineSamples = Cluster(100);
        var candidateSamples = Cluster(50);

        var results = new List<BenchmarkResult>
        {
            Result("baseline", true) with { Mean = 100, Median = 100 },
            Result("candidate") with { Mean = 50, Median = 50 },
        };
        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        Significance.ComputeSignificance(results, rawSamples, 0.05,
            minimumPracticalEffect: 0, minimumRelativeShift: 0);

        var candidate = results.Single(r => r.Name == "candidate");

        Assert.Equal(SignificanceVerdict.Significant, candidate.SignificanceVerdict);
        Assert.Equal(SignificanceVerdict.NotTested, candidate.LaunchBlockedVerdict);
        Assert.DoesNotContain(candidate.Warnings, w => w.Contains("reproducibility"));
    }

    /// <summary>
    ///     Pooled raw samples drawn as a cluster around each of <paramref name="centers" />, in order,
    ///     40 samples per centre. Models <c>PoolRawSamplesByName</c> concatenating several launches
    ///     whose per-launch medians sit at those centres.
    /// </summary>
    private static double[] Mixture(params double[] centers)
    {
        var rng = new Random(7);
        var list = new List<double>(centers.Length * 40);
        foreach (var center in centers)
        {
            for (var i = 0; i < 40; i++)
                list.Add(center + rng.NextDouble());
        }
        return list.ToArray();
    }

    /// <summary>
    ///     A <see cref="LaunchStatistics" /> whose per-launch medians are <paramref name="medians" />,
    ///     with the summary fields set consistently so the launch-aware baseline selector and the
    ///     reproducibility ratio both see honest numbers.
    /// </summary>
    private static LaunchStatistics Launches(params double[] medians)
    {
        var sorted = medians.OrderBy(m => m).ToArray();
        var mid = sorted.Length / 2;
        var launchMedian = sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
        var launchMean = medians.Average();
        var launchStdDev = medians.Length > 1
            ? Math.Sqrt(medians.Sum(m => (m - launchMean) * (m - launchMean)) / (medians.Length - 1))
            : 0;

        return new LaunchStatistics
        {
            LaunchCount = medians.Length,
            LaunchMean = launchMean,
            LaunchStandardDeviation = launchStdDev,
            LaunchMedian = launchMedian,
            Launches = medians
                .Select((m, i) => new LaunchDetail
                {
                    LaunchIndex = i,
                    Median = m,
                    Mean = m,
                    StandardDeviation = 0.1,
                    Iterations = 100,
                    Duration = TimeSpan.FromSeconds(1),
                })
                .ToList(),
        };
    }

    private static double[] Cluster(double center)
    {
        var rng = new Random((int)center + 1);
        return Enumerable.Range(0, 40).Select(_ => center + rng.NextDouble()).ToArray();
    }

    /// <summary>A cluster shifted by <paramref name="offset" /> from <paramref name="center" />,
    /// sharing the center's seed so two calls at the same center overlap predictably. Used to build
    /// the partial-overlap cases that produce moderate (non-tiny) Mann-Whitney p-values.</summary>
    private static double[] Cluster(double center, double offset)
    {
        var rng = new Random((int)center + 1);
        return Enumerable.Range(0, 40).Select(_ => center + offset + rng.NextDouble()).ToArray();
    }

    private static BenchmarkResult Result(string name, bool isBaseline = false) => new()
    {
        Name = name,
        Mean = 0,
        Median = 0,
        Percentiles = [],
        Min = 0,
        Max = 0,
        StandardDeviation = 0,
        IsBaseline = isBaseline,
        Q1 = 0,
        Q3 = 0,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 0,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
    };

    private sealed class FixedSignificanceTest(string candidate, double pValue) : ISignificanceTest
    {
        public string Name => "fixed";

        public SignificanceReport Analyze(SignificanceContext context) => new()
        {
            Pairwise = [new PairwiseComparison(candidate, pValue, SignificanceVerdict.Significant)],
        };
    }

    private sealed class DeltaReportingSignificanceTest(string candidate, double delta, double pValue) : ISignificanceTest
    {
        public string Name => "delta-reporting";

        public SignificanceReport Analyze(SignificanceContext context) => new()
        {
            Pairwise =
            [
                new PairwiseComparison(
                    candidate,
                    pValue,
                    SignificanceVerdict.Significant,
                    new EffectSize(
                        "custom-delta",
                        delta,
                        MagnitudeLabelExtensions.Classify(Math.Abs(delta)).ToShortString(),
                        delta switch
                        {
                            > 0 => EffectDirection.CandidateHigher,
                            < 0 => EffectDirection.CandidateLower,
                            _ => EffectDirection.None,
                        },
                        Math.Abs(delta))),
            ],
        };
    }
}
