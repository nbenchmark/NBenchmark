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
        Assert.Same(custom, (MeasurementOptions.Default with { SignificanceTest = custom }).ResolveSignificanceTest());
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

    private static double[] Cluster(double center)
    {
        var rng = new Random((int)center + 1);
        return Enumerable.Range(0, 40).Select(_ => center + rng.NextDouble()).ToArray();
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
