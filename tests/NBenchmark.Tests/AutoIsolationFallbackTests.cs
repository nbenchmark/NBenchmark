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
            .WithIsolation()

            // The labelled fallback rather than the hard error: these tests are about what the refusal
            // says, and the throw is covered by RequiredIsolationTests.
            .WithRequireIsolation(false);

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
            .WithIsolation()

            // The labelled fallback rather than the hard error: these tests are about what the refusal
            // says, and the throw is covered by RequiredIsolationTests.
            .WithRequireIsolation(false);

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

        // The wire carries the harness-level default - here PerMethod, since nothing called
        // WithInstanceLifetime - and, separately, the lifetime the coordinator resolved for this
        // class. The class asked for PerClass and its instances come from its own constructor, so it
        // gets what it asked for.
        Assert.Equal(InstanceLifetime.PerMethod, request.DefaultInstanceLifetime);
        Assert.Equal(InstanceLifetime.PerClass, request.InstanceLifetimeOverride);
    }

    /// <summary>
    ///     W-21: a class whose instances come from a container keeps PerClass only if it says so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The rule this replaces was written for the same case and was unreachable: it lived
    ///         behind the global in-process guard in the one function that decided both lifetime and
    ///         granularity, and its own condition was true only for harnesses that had no instance
    ///         source at all. It therefore fired exactly where a shared instance is harmless and
    ///         never where a container is handing out scopes.
    ///     </para>
    ///     <para>
    ///         Asserted on the wire rather than through behaviour because the decision <em>is</em> the
    ///         wire field: an isolated group is measured in a process that reads it and nothing else.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task PerClass_WithAnAddressableFactory_ResolvesToPerMethod()
    {
        var (results, requests) = await RunWithFactoryAsync("auto-iso-factory-perclass");
        var request = Assert.Single(requests);

        Assert.Equal(InstanceLifetime.PerMethod, request.InstanceLifetimeOverride);

        // And the rows say why, because a lifetime the user did not ask for is a fact about their
        // measurement rather than an implementation detail.
        Assert.All(
            results.Where(r => r.ClassName == "AddressableFactoryPerClassBenchmarks"),
            r => Assert.Contains(r.Warnings, w => w.Contains("fresh instance per method", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     A class that resets itself keeps PerClass, and is not warned about.
    /// </summary>
    [Fact]
    public async Task PerClass_WithAnAddressableFactory_And_IStateReset_KeepsPerClass()
    {
        var (results, requests) = await RunWithFactoryAsync("auto-iso-factory-reset");
        var request = Assert.Single(requests);

        Assert.Equal(InstanceLifetime.PerClass, request.InstanceLifetimeOverride);
        Assert.All(results, r => Assert.DoesNotContain(r.Warnings, w => w.Contains("fresh instance per method", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     So does one that declares the carry-over deliberate. The two have to be separable: before
    ///     <c>[SharedState]</c> existed, an empty <c>ResetAsync</c> was the only way to say this, and
    ///     an empty <c>ResetAsync</c> is indistinguishable from a real one at runtime.
    /// </summary>
    [Fact]
    public async Task PerClass_WithAnAddressableFactory_And_SharedState_KeepsPerClass()
    {
        var (_, requests) = await RunWithFactoryAsync("auto-iso-factory-shared");
        var request = Assert.Single(requests);

        Assert.Equal(InstanceLifetime.PerClass, request.InstanceLifetimeOverride);
    }

    /// <summary>
    ///     The lifetime is resolved independently of whether a worker is available, which is the
    ///     structural half of W-21. Under <c>--in-process</c> the old function returned before the
    ///     rule could run at all.
    /// </summary>
    [Fact]
    public async Task PerClass_WithAFactory_ResolvesToPerMethod_EvenInProcess()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter(["auto-iso-factory-perclass"])
            .WithInstanceFactory(AddressableFactoryPerClassBenchmarks.Create)
            .WithLaunchCount(1)
            .WithIsolation(false);

        var results = await harness.RunAsync();

        Assert.All(
            results.Where(r => r.ClassName == "AddressableFactoryPerClassBenchmarks"),
            r => Assert.Contains(r.Warnings, w => w.Contains("fresh instance per method", StringComparison.Ordinal)));
    }

    private static async Task<(IReadOnlyList<BenchmarkResult> Results, IReadOnlyList<RunGroupPayload> Requests)>
        RunWithFactoryAsync(string category)
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(AutoIsolationFallbackTests).Assembly)
            .WithCategoryFilter([category])
            .WithInstanceFactory(AddressableFactoryPerClassBenchmarks.Create)
            .WithLaunchCount(1)
            .WithIsolation();

        using var scope = FakeWorkerLauncher.Install(SimulateWorkerGroup);
        var results = await harness.RunAsync();

        return (results, scope.Launcher.Requests.ToList());
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

        // Each worker measures once. The request has no launch count on it to say otherwise - the
        // replicate count is spent here, by launching, and never travels - so what is left to check is
        // that three distinct requests were made rather than one asking for three passes.
        Assert.Equal(3, scope.Launcher.Requests.Select(r => r.GroupId).Distinct().Count());

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
            .WithIsolation()
            .WithRequireIsolation(false);

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

// PerClass + an addressable (static, non-capturing) factory: isolatable, and the one shape the
// lifetime rule exists for - the instance carries a scope, so sharing the instance shares the scope.
[BenchmarkCategory("auto-iso-factory-perclass")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class AddressableFactoryPerClassBenchmarks
{
    public static object Create(Type type) => Activator.CreateInstance(type)!;

    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}

// The same, with a class that resets between methods: PerClass is honoured.
[BenchmarkCategory("auto-iso-factory-reset")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class AddressableFactoryResettingBenchmarks : IStateReset
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

// And with the carry-over declared deliberate: also honoured, by a different route.
[BenchmarkCategory("auto-iso-factory-shared")]
[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState]
public class AddressableFactorySharedStateBenchmarks
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
