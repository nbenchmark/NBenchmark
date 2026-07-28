using NBenchmark.Attributes;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;
using NBenchmark.Tests.Workers;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     How the harness decides between a measurement worker and the host process, and what it says
///     when it picks the host.
/// </summary>
/// <remarks>
///     These tests previously asserted the shape of child-process launches for factory-resolved
///     instances. That combination no longer reaches a child at all: a worker does not re-run the
///     user's entry point, so it has no way to obtain a factory that lives in the coordinator's
///     object graph, and constructing the type directly instead would measure a
///     differently-configured object while reporting it as though nothing had changed. The contract
///     under test is therefore now the refusal - and, importantly, that the refusal is <em>visible</em>.
/// </remarks>
public class AutoIsolationFallbackTests
{
    /// <summary>
    ///     A harness with an instance factory measures in the host process and never asks for a
    ///     worker. Silently measuring the wrong object would be the worst available outcome; measuring
    ///     the right object less accurately, and saying so, is the least bad one.
    /// </summary>
    [Fact]
    public async Task InstanceFactory_IsNotIsolated_AndSaysSo()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-fallback"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        using var scope = FakeWorkerLauncher.Install(SimulateWorkerGroup);
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await harness.RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Empty(scope.Launcher.Requests);

        var classResults = results.Where(r => r.ClassName == "FactoryNoResetBenchmarks").ToList();
        Assert.Equal(2, classResults.Count);

        // The provenance is on every result, so it survives even if the console message scrolls by.
        Assert.All(classResults, r => Assert.Equal("host", r.RuntimeProfileName));

        var message = stderr.ToString();
        Assert.Contains("FactoryNoResetBenchmarks", message);
        Assert.Contains("instance factory", message);
        Assert.Contains("host", message);
    }

    /// <summary>
    ///     The refusal is reported once per class, not once per benchmark. A message that repeats for
    ///     every row is a message people learn to skip.
    /// </summary>
    [Fact]
    public async Task IsolationRefusal_IsReportedOncePerClass()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-fallback"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation();

        using var scope = FakeWorkerLauncher.Install(SimulateWorkerGroup);
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            await harness.RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        var occurrences = stderr.ToString().Split("FactoryNoResetBenchmarks").Length - 1;
        Assert.Equal(1, occurrences);
    }

    /// <summary>
    ///     Without a factory, the whole class goes to one worker: the group, not the individual
    ///     benchmark, is the unit of isolation. That makes every ratio within the group a paired,
    ///     within-process estimate, so the worker's CPU draw and thermal state cancel out of it
    ///     instead of inflating its variance.
    /// </summary>
    [Fact]
    public async Task NoFactory_PerClass_GoesToOneWorkerWithBothBenchmarks()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-nofactory"])
            .WithLaunchCount(1)
            .WithIsolation();

        using var scope = FakeWorkerLauncher.Install(SimulateWorkerGroup);
        await harness.RunAsync();

        var request = Assert.Single(scope.Launcher.Requests);
        Assert.Equal(WorkGroupKind.DiscoveredClass, request.Kind);
        Assert.Equal(2, request.BenchmarkNames.Count);

        // The wire carries the *harness-level* default only - here PerMethod, since nothing called
        // WithInstanceLifetime. This class's [InstanceLifetime(PerClass)] is resolved by discovery
        // inside the worker, which is why none of that machinery has to cross the boundary.
        Assert.Equal(InstanceLifetime.PerMethod, request.DefaultInstanceLifetime);
    }

    /// <summary>
    ///     <c>[InProcess]</c> on a method keeps that one benchmark in the host while its siblings go
    ///     to a worker, so a benchmark that genuinely needs to observe the host process still can.
    /// </summary>
    [Fact]
    public async Task InProcessAttribute_KeepsOnlyThatBenchmarkInTheHost()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-inprocess-nofactory"])
            .WithLaunchCount(1)
            .WithIsolation();

        using var scope = FakeWorkerLauncher.Install(SimulateWorkerGroup);
        var results = await harness.RunAsync();

        var request = Assert.Single(scope.Launcher.Requests);
        Assert.Equal(["IsolatedMethod"], request.BenchmarkNames);

        // The in-process one is stamped host; the isolated one carries whatever the worker reported.
        var inProcess = results.Single(r => r.Name.EndsWith(".InProcessMethod", StringComparison.Ordinal));
        Assert.Equal("host", inProcess.RuntimeProfileName);
    }

    /// <summary>
    ///     Each replicate is a fresh worker with a distinct shuffle seed. The previous isolated path
    ///     hardcoded declaration order, so <see cref="RunOrder.Random" /> was silently discarded
    ///     whenever isolation was on - which, in Harness mode, is always.
    /// </summary>
    [Fact]
    public async Task LaunchCount_SpawnsOneWorkerPerReplicate_WithDistinctSeeds()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-nofactory"])
            .WithLaunchCount(3)
            .WithRunOrder(RunOrder.Random)
            .WithIsolation();

        using var scope = FakeWorkerLauncher.Install(SimulateWorkerGroup);
        await harness.RunAsync();

        Assert.Equal(3, scope.Launcher.Requests.Count);
        Assert.All(scope.Launcher.Requests, r => Assert.Equal(RunOrder.Random, r.Order));

        // Each worker measures once; LaunchCount is spent on workers, not repeated inside one.
        Assert.All(scope.Launcher.Requests, r => Assert.Equal(1, r.Options.LaunchCount));

        // With no pinned session seed, each replicate picks its own order in the worker, so the
        // request carries no seed. Seed derivation itself is covered directly below.
        Assert.All(scope.Launcher.Requests, r => Assert.Null(r.Seed));
    }

    /// <summary>
    ///     With no worker deployed, the run still happens - less accurately - and explains itself.
    ///     A packaging problem should not fail a benchmark run outright.
    /// </summary>
    [Fact]
    public async Task NoWorkerDeployed_FallsBackToTheHost_AndExplains()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-nofactory"])
            .WithLaunchCount(1)
            .WithIsolation();

        using var _ = FakeWorkerLauncher.InstallUnavailable();
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await harness.RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal("host", r.RuntimeProfileName));
        Assert.Contains("nbworker", stderr.ToString());
    }

    /// <summary>
    ///     Synthesizes what a worker would return, so these tests exercise the coordinator's planning
    ///     rather than a real process launch. The real path is covered by <see cref="RealWorkerTests" />.
    /// </summary>
    private static WorkerGroupRunner.GroupResult SimulateWorkerGroup(RunGroupPayload request)
    {
        var results = new List<BenchmarkResult>();
        var samples = new Dictionary<string, double[]>(StringComparer.Ordinal);

        foreach (var displayName in request.BenchmarkNames)
        {
            var fullName = string.IsNullOrEmpty(request.DisplayPrefix)
                ? displayName
                : $"{request.DisplayPrefix}.{displayName}";

            const double median = 100.0;
            var rawSamples = Enumerable.Range(0, 10).Select(i => 100.0 + i * 0.1).ToArray();

            results.Add(new BenchmarkResult
            {
                Name = fullName,
                ClassName = request.DisplayPrefix,
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
                RuntimeProfileName = request.Options.RuntimeProfile.Name,
                RuntimeKnobs = request.Options.RuntimeProfile.Describe(),
                Errored = false,
            });

            samples[fullName] = rawSamples;
        }

        return new WorkerGroupRunner.GroupResult { Results = results, RawSamples = samples, Faults = [] };
    }
}

// PerClass + factory: cannot be isolated, because a worker cannot reproduce the factory.
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

// PerClass + factory + IStateReset: still not isolatable - IStateReset governs shared-instance
// hygiene inside a group, not whether the group's instances can be built in another process.
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

// [InProcess] on one method, with a factory.
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

// [InProcess] on one method, no factory: the sibling goes to a worker, this one stays in the host.
[BenchmarkCategory("auto-iso-inprocess-nofactory")]
public class MixedWorkerIsolationBenchmarks
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

// PerClass, no factory: goes to one worker with both benchmarks.
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
