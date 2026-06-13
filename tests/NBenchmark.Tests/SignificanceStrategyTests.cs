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
    public void Default_ThreeGroups_UsesKruskalWallisOmnibus()
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

        Assert.Empty(report.Pairwise);
        Assert.NotNull(report.Omnibus);
        Assert.Equal("Kruskal-Wallis", report.Omnibus!.TestName);
        Assert.Equal(3, report.Omnibus.GroupCount);
        Assert.Equal(2, report.Omnibus.DegreesOfFreedom);
        Assert.Equal(SignificanceVerdict.Significant, report.Omnibus.Verdict);
    }

    [Fact]
    public void ComputeSignificance_ThreeGroups_AttachesOmnibusToEveryResult()
    {
        var results = new List<BenchmarkResult>
        {
            Result("a", isBaseline: true),
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
        // Omnibus comparisons leave per-row pairwise verdicts untested.
        Assert.All(results, r => Assert.Equal(SignificanceVerdict.NotTested, r.SignificanceVerdict));
    }

    [Fact]
    public void ComputeSignificance_TwoGroups_SetsPairwiseVerdictAndNoOmnibus()
    {
        var results = new List<BenchmarkResult>
        {
            Result("baseline", isBaseline: true),
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
            Result("baseline", isBaseline: true),
            Result("candidate"),
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Cluster(10),
            ["candidate"] = Cluster(11),
        };

        var custom = new FixedSignificanceTest("candidate", pValue: 0.001);

        Significance.ComputeSignificance(results, rawSamples, custom);

        Assert.Equal(0.001, results[1].PValue);
        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_WarnsAndExcludes_CandidateWithMissingSamples()
    {
        var results = new List<BenchmarkResult>
        {
            Result("baseline", isBaseline: true),
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
            Result("baseline", isBaseline: true),
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
        P95 = 0,
        P99 = 0,
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
}
