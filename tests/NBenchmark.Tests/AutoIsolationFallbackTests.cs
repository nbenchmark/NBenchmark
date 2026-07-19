using NBenchmark.Attributes;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;
using Xunit;

namespace NBenchmark.Tests;

public class AutoIsolationFallbackTests
{
    [Fact]
    public async Task PerClass_Factory_NoIStateReset_AutoUpgrades_To_PerBenchmark()
    {
        // b-factory rule: PerClass + factory + no IStateReset -> PerBenchmark.
        // A 2-method class should produce 2 separate child launches (one per benchmark),
        // not 1 launch with both benchmarks. Each request has 1 display name.
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var launches = new List<IsolatedRunRequest>();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-fallback"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        using (WithFakeLauncher((req, ct) =>
               {
                   launches.Add(req);
                   return Task.FromResult<IReadOnlyList<IsolatedResultItem>>(SimulateChildRun(req));
               }))
        {
            var results = await harness.RunAsync();
        }

        // Two benchmarks, auto-upgraded to PerBenchmark -> 2 launches, each with 1 display name.
        Assert.Equal(2, launches.Count);
        Assert.All(launches, l => Assert.Single(l.BenchmarkDisplayNames));
    }

    [Fact]
    public async Task PerClass_Factory_NoIStateReset_Attaches_UpgradeWarning_ToResults()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-fallback"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        IReadOnlyList<BenchmarkResult> results;

        using (WithFakeLauncher((req, ct) =>
                   Task.FromResult<IReadOnlyList<IsolatedResultItem>>(SimulateChildRun(req))))
        {
            results = await harness.RunAsync();
        }

        var classResults = results.Where(r => r.ClassName == "FactoryNoResetBenchmarks").ToList();
        Assert.Equal(2, classResults.Count);

        // Each result from the auto-upgraded class carries the upgrade warning.
        Assert.All(classResults, r =>
        {
            var warning = r.Warnings.FirstOrDefault(w => w.Contains("upgrading to per-benchmark isolated process"));
            Assert.NotNull(warning);
            Assert.Contains("IStateReset", warning);
        });
    }

    [Fact]
    public async Task PerClass_Factory_WithIStateReset_Stays_PerClass_InProcess()
    {
        // When the class implements IStateReset, the b-factory rule does NOT upgrade.
        // With isolation enabled, PerClass stays PerClass (one child for both methods).
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var launches = new List<IsolatedRunRequest>();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-reset"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        using (WithFakeLauncher((req, ct) =>
               {
                   launches.Add(req);
                   return Task.FromResult<IReadOnlyList<IsolatedResultItem>>(SimulateChildRun(req));
               }))
        {
            var results = await harness.RunAsync();
        }

        // PerClass with IStateReset -> 1 launch with both display names (not upgraded).
        Assert.Single(launches);
        Assert.Equal(2, launches[0].BenchmarkDisplayNames.Count);
    }

    [Fact]
    public async Task PerClass_Factory_WithIStateReset_No_UpgradeWarning()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-reset"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        IReadOnlyList<BenchmarkResult> results;

        using (WithFakeLauncher((req, ct) =>
                   Task.FromResult<IReadOnlyList<IsolatedResultItem>>(SimulateChildRun(req))))
        {
            results = await harness.RunAsync();
        }

        var classResults = results.Where(r => r.ClassName == "FactoryWithResetBenchmarks").ToList();
        Assert.Equal(2, classResults.Count);

        // No upgrade warning because IStateReset is implemented.
        Assert.All(classResults, r =>
        {
            var upgradeWarning = r.Warnings.FirstOrDefault(w => w.Contains("upgrading to per-benchmark isolated process"));
            Assert.Null(upgradeWarning);
        });
    }

    [Fact]
    public async Task PerClass_Factory_With_InProcess_Attribute_Stays_InProcess()
    {
        // Explicit [InProcess] on the method wins over the auto-upgrade rule.
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-inprocess"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        IReadOnlyList<BenchmarkResult> results;

        using (WithFakeLauncher((req, ct) =>
                   Task.FromResult<IReadOnlyList<IsolatedResultItem>>(SimulateChildRun(req))))
        {
            results = await harness.RunAsync();
        }

        // The [InProcess] method runs in-process, so no isolated result for it.
        // The other method (no [InProcess]) is auto-upgraded to PerBenchmark.
        var classResults = results.Where(r => r.ClassName == "FactoryInProcessBenchmarks").ToList();
        Assert.Equal(2, classResults.Count);

        // The InProcess method should NOT carry the upgrade warning (it stayed in-process).
        var inProcessResult = classResults.Single(r => r.Name.EndsWith(".InProcessMethod"));
        var upgradeWarning = inProcessResult.Warnings.FirstOrDefault(w => w.Contains("upgrading to per-benchmark isolated process"));
        Assert.Null(upgradeWarning);
    }

    [Fact]
    public async Task PerClass_NoFactory_NoIStateReset_Stays_PerClass()
    {
        // Without a factory, the b-factory rule does not fire. PerClass stays PerClass.
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var launches = new List<IsolatedRunRequest>();

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-nofactory"])
            .WithLaunchCount(1)
            .WithIsolation();

        using (WithFakeLauncher((req, ct) =>
               {
                   launches.Add(req);
                   return Task.FromResult<IReadOnlyList<IsolatedResultItem>>(SimulateChildRun(req));
               }))
        {
            var results = await harness.RunAsync();
        }

        // No factory -> PerClass stays PerClass -> 1 launch with both display names.
        Assert.Single(launches);
        Assert.Equal(2, launches[0].BenchmarkDisplayNames.Count);
    }

    private static IReadOnlyList<IsolatedResultItem> SimulateChildRun(IsolatedRunRequest request)
    {
        var items = new List<IsolatedResultItem>();
        var prefix = request.DisplayPrefix;

        foreach (var displayName in request.BenchmarkDisplayNames)
        {
            var fullName = string.IsNullOrEmpty(prefix) ? displayName : $"{prefix}.{displayName}";
            const double median = 100.0;
            var rawSamples = Enumerable.Repeat(100.0, 10).Select((_, i) => 100.0 + i * 0.1).ToArray();

            var result = new BenchmarkResult
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
                IsBaseline = false,
                Errored = false,
            };

            items.Add(new IsolatedResultItem
            {
                Result = result,
                RawSamples = rawSamples,
            });
        }

        return items;
    }

    private static IDisposable WithFakeLauncher(
        Func<IsolatedRunRequest, CancellationToken, Task<IReadOnlyList<IsolatedResultItem>>> handler)
    {
        var prior = ChildProcessLauncher.Current;
        ChildProcessLauncher.Current = new FakeProcessLauncher(handler);
        return new Restorer(prior);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly IProcessLauncher _prior;
        private bool _disposed;

        public Restorer(IProcessLauncher prior)
        {
            _prior = prior;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            ChildProcessLauncher.Current = _prior;
            _disposed = true;
        }
    }
}

// PerClass + factory + no IStateReset -> should auto-upgrade to PerBenchmark.
[BenchmarkCategory("auto-iso-fallback")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class FactoryNoResetBenchmarks
{
    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}

// PerClass + factory + IStateReset -> should stay PerClass.
[BenchmarkCategory("auto-iso-reset")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class FactoryWithResetBenchmarks : IStateReset
{
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}

// PerClass + factory + [InProcess] on one method -> that method stays in-process.
[BenchmarkCategory("auto-iso-inprocess")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class FactoryInProcessBenchmarks
{
    [Benchmark]
    [InProcess]
    public void InProcessMethod()
    {
    }

    [Benchmark]
    public void IsolatedMethod()
    {
    }
}

// PerClass + no factory + no IStateReset -> should stay PerClass (no factory to trigger rule).
[BenchmarkCategory("auto-iso-nofactory")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class NoFactoryBenchmarks
{
    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}
