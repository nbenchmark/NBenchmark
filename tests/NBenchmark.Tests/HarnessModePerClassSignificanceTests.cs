using NBenchmark.Attributes;
using NBenchmark.Engine;
using NBenchmark.Tests.Workers;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

public class HarnessModePerClassSignificanceTests
{
    [Fact]
    public async Task HarnessIsolated_PerClassSignificance_EachClassHasOwnBaseline()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness
            .AddFromAssembly(typeof(HarnessModePerClassSignificanceTests).Assembly)
            .WithCategoryFilter(["harness-perclass"])
            .WithLaunchCount(1)
            .WithOptions(new MeasurementOptions
            {
                Iterations = 20,
                WarmupIterations = 0,
                OutlierMode = OutlierMode.None,
            })
            .WithIsolation();

        using (FakeWorkerLauncher.Install(SimulateWorkerGroup))
        {
            var results = await harness.RunAsync();

            Assert.Equal(5, results.Count);

            var alphaResults = results.Where(r => r.ClassName == "AlphaBenchmarks").ToList();
            var betaResults = results.Where(r => r.ClassName == "BetaBenchmarks").ToList();

            Assert.Equal(2, alphaResults.Count);
            Assert.Equal(3, betaResults.Count);

            var alphaBaseline = alphaResults.Single(r => r.IsBaseline);
            Assert.Equal("AlphaBenchmarks.AlphaFast", alphaBaseline.Name);

            var alphaSlow = alphaResults.Single(r => !r.IsBaseline);

            Assert.True(alphaSlow.SignificanceVerdict == SignificanceVerdict.Significant,
                $"Expected AlphaSlow to be significant versus AlphaFast; got {alphaSlow.SignificanceVerdict} (p={alphaSlow.PValue})");

            Assert.NotNull(alphaSlow.Effect);

            var betaBaseline = betaResults.Single(r => r.IsBaseline);
            Assert.Equal("BetaBenchmarks.BetaSmall", betaBaseline.Name);

            foreach (var candidate in betaResults.Where(r => !r.IsBaseline))
            {
                Assert.True(candidate.SignificanceVerdict == SignificanceVerdict.Significant,
                    $"Expected {candidate.Name} to be significant versus BetaSmall; got {candidate.SignificanceVerdict} (p={candidate.PValue})");

                Assert.NotNull(candidate.Effect);
            }
        }
    }

    [Fact]
    public async Task HarnessIsolated_CrossClassSignificance_SingleBaselineAcrossClasses()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness
            .AddFromAssembly(typeof(HarnessModePerClassSignificanceTests).Assembly)
            .WithCategoryFilter(["harness-perclass"])
            .WithLaunchCount(1)
            .WithOptions(new MeasurementOptions
            {
                Iterations = 20,
                WarmupIterations = 0,
                OutlierMode = OutlierMode.None,
            })
            .WithIsolation()
            .WithCrossClassSignificance();

        using (FakeWorkerLauncher.Install(SimulateWorkerGroup))
        {
            var results = await harness.RunAsync();

            Assert.Equal(5, results.Count);

            // In cross-class mode, both AlphaFast and BetaSmall carry IsBaseline=true
            // (they are declared [Benchmark(Baseline = true)]). Significance is computed
            // against the first explicit baseline in the group. BetaSmall becomes a
            // candidate compared against AlphaFast rather than its own class baseline.
            var baselines = results.Where(r => r.IsBaseline).ToList();
            Assert.Equal(2, baselines.Count);
            Assert.Contains(baselines, r => r.Name == "AlphaBenchmarks.AlphaFast");
            Assert.Contains(baselines, r => r.Name == "BetaBenchmarks.BetaSmall");

            // BetaSmall was a per-class baseline (median 100, same as AlphaFast). In
            // cross-class mode it is compared against AlphaFast and should be NotSignificant
            // (identical distributions).
            var betaSmall = results.Single(r => r.Name == "BetaBenchmarks.BetaSmall");
            Assert.Equal(SignificanceVerdict.NotSignificant, betaSmall.SignificanceVerdict);

            // The genuinely slower benchmarks should be significant versus the global baseline.
            foreach (var candidate in results.Where(r => !r.IsBaseline))
            {
                Assert.True(candidate.SignificanceVerdict == SignificanceVerdict.Significant,
                    $"Expected {candidate.Name} to be significant vs global baseline; got {candidate.SignificanceVerdict} (p={candidate.PValue})");

                Assert.NotNull(candidate.Effect);
            }
        }
    }

    /// <summary>
    ///     Synthesizes what a worker would send back for a group: one completion per requested
    ///     benchmark, each carrying its own raw samples. The samples are deterministic so the
    ///     significance assertions below test the coordinator's statistics rather than the machine's.
    /// </summary>
    private static WorkerGroupRunner.GroupResult SimulateWorkerGroup(RunGroupPayload request)
    {
        var results = new List<BenchmarkResult>();
        var samples = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var prefix = request.DisplayPrefix;

        foreach (var displayName in request.BenchmarkNames)
        {
            var fullName = string.IsNullOrEmpty(prefix) ? displayName : $"{prefix}.{displayName}";
            var (median, rawSamples, isBaseline) = GetDeterministicData(prefix, displayName);

            results.Add(new BenchmarkResult
            {
                Name = fullName,
                ClassName = prefix,
                Mean = median,
                Median = median,
                Percentiles = [],
                Min = median * 0.95,
                Max = median * 1.10,
                StandardDeviation = median * 0.02,
                StandardError = median * 0.005,
                MarginOfError = median * 0.01,
                ConfidenceLevel = 0.95,
                CoefficientOfVariation = 0.02,
                Q1 = median * 0.98,
                Q3 = median * 1.02,
                InterquartileRange = median * 0.04,
                OutliersRemoved = 0,
                N = rawSamples.Length,
                Skewness = 0,
                Kurtosis = 0,
                Mad = median * 0.01,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
                OperationsPerSecond = 1_000_000_000.0 / median,
                MeasuredIterations = rawSamples.Length,
                WarmupIterations = 0,
                IsBaseline = isBaseline,
                Errored = false,
            });

            samples[fullName] = rawSamples;
        }

        return new WorkerGroupRunner.GroupResult { Results = results, RawSamples = samples, Faults = [] };
    }

    private static (double Median, double[] RawSamples, bool IsBaseline) GetDeterministicData(
        string prefix,
        string displayName)
    {
        if (prefix == "AlphaBenchmarks")
        {
            return displayName switch
            {
                "AlphaFast" => (100.0, Enumerable.Repeat(100.0, 20).Select((_, i) => 100.0 + i * 0.1).ToArray(), true),
                "AlphaSlow" => (500.0, Enumerable.Repeat(500.0, 20).Select((_, i) => 500.0 + i * 0.5).ToArray(), false),
                _ => throw new ArgumentOutOfRangeException(nameof(displayName)),
            };
        }

        return displayName switch
        {
            "BetaSmall" => (100.0, Enumerable.Repeat(100.0, 20).Select((_, i) => 100.0 + i * 0.1).ToArray(), true),
            "BetaMedium" => (300.0, Enumerable.Repeat(300.0, 20).Select((_, i) => 300.0 + i * 0.3).ToArray(), false),
            "BetaLarge" => (900.0, Enumerable.Repeat(900.0, 20).Select((_, i) => 900.0 + i * 0.9).ToArray(), false),
            _ => throw new ArgumentOutOfRangeException(nameof(displayName)),
        };
    }

}

[BenchmarkCategory("harness-perclass")]
public class AlphaBenchmarks
{
    [Benchmark(Baseline = true)]
    public void AlphaFast() => Thread.SpinWait(32);

    [Benchmark]
    public void AlphaSlow() => Thread.SpinWait(256);
}

[BenchmarkCategory("harness-perclass")]
public class BetaBenchmarks
{
    [Benchmark(Baseline = true)]
    public void BetaSmall() => Thread.SpinWait(16);

    [Benchmark]
    public void BetaMedium() => Thread.SpinWait(128);

    [Benchmark]
    public void BetaLarge() => Thread.SpinWait(512);
}
