using NBenchmark.Attributes;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A DI-backed harness run stays isolated when the container is supplied as a factory.
/// </summary>
/// <remarks>
///     <para>
///         A live <see cref="IServiceProvider" /> cannot cross a process boundary, so passing one costs
///         the run its isolation - correctly, because constructing the benchmark type directly instead
///         would measure an object with none of its dependencies configured and report it under the
///         right name. A static factory is a <i>recipe</i> for a container, and a recipe is addressable:
///         the worker runs it and resolves instances from the container it built itself.
///     </para>
///     <para>
///         Written because the first implementation of this shipped broken in a way no unit test would
///         have caught. The worker resolved the factory through a <i>second</i>
///         <c>BenchmarkLoadContext</c>, so the type the container registered and the type discovery
///         asked for were two distinct <see cref="Type" /> identities for the same class, and every
///         resolution failed with "no service of type ... is registered". Only running the real thing
///         showed it.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class ServiceProviderFactoryIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public ServiceProviderFactoryIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static BenchmarkHarness Harness()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        return harness
            .AddFromAssembly(typeof(ServiceProviderFactoryIsolationTests).Assembly)
            .WithCategoryFilter(["sp-factory"])
            .WithLaunchCount(1)
            .WithOptions(MeasurementOptions.Default with
            {
                Iterations = 16,
                WarmupIterations = 1,
                AutoTune = AutoTuneOptions.Default with
                {
                    MaxTuningTime = TimeSpan.FromSeconds(5),
                    MinWarmupTime = TimeSpan.Zero,
                    MinMeasurementTime = TimeSpan.Zero,
                    RequireJitQuiescence = false,
                    EnableJitterCalibration = false,
                },
            });
    }

    /// <summary>
    ///     The container is built in the worker, and the instance it resolved is the configured one.
    /// </summary>
    /// <remarks>
    ///     The value assertion is the load-bearing half. <c>Probe</c> is registered with a distinctive
    ///     payload, and the benchmark returns it - so a worker that had fallen back to a parameterless
    ///     construction, or resolved from a differently-registered container, would report a different
    ///     number rather than failing.
    /// </remarks>
    [Fact]
    public async Task ServiceProviderFactory_IsIsolated_AndResolvesTheConfiguredInstance()
    {
        var results = await Harness()
            .WithServiceProvider(BuildProvider)
            .RunAsync();

        var result = Assert.Single(results, r => r.ClassName == nameof(InjectedBenchmarks));

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
        Assert.Equal("steady-state", result.RuntimeProfileName);
        Assert.NotEmpty(result.RawSamples);

        // Cost scales with the injected payload, so the worker resolved the configured dependency.
        Assert.True(
            result.Median > 5_000,
            $"expected the injected spin count to dominate, but measured {result.Median:F1} ns");
    }

    /// <summary>
    ///     A live provider still refuses, so the factory is what changed the outcome and nothing else.
    /// </summary>
    [Fact]
    public async Task LiveServiceProvider_StillRefuses_AndPointsAtTheFactory()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Harness()
                .WithServiceProvider(BuildProvider())
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        var result = Assert.Single(results, r => r.ClassName == nameof(InjectedBenchmarks));

        Assert.NotEqual(IsolationStatus.Isolated, result.IsolationStatus);

        // The refusal names the way out, which is the only reason a user would find it.
        Assert.Contains("WithServiceProvider(BuildServices)", stderr.ToString());
    }

    /// <summary>
    ///     A capturing factory is refused, because a container built here is the live object that cannot
    ///     cross in the first place.
    /// </summary>
    [Fact]
    public async Task CapturingServiceProviderFactory_RefusesAndSaysSo()
    {
        var spins = 20_000;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Harness()
                .WithServiceProvider(() => new SingleServiceProvider(new Probe(spins)))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        var result = Assert.Single(results, r => r.ClassName == nameof(InjectedBenchmarks));

        Assert.NotEqual(IsolationStatus.Isolated, result.IsolationStatus);

        var message = stderr.ToString();
        Assert.Contains("captures", message);
        Assert.Contains("static", message);
    }

    /// <summary>
    ///     The addressable recipe. Static and non-capturing, which is what the whole mechanism requires.
    /// </summary>
    private static IServiceProvider BuildProvider() => new SingleServiceProvider(new Probe(20_000));

    /// <summary>
    ///     A minimal container, so these tests exercise the core mechanism rather than a DI package's
    ///     behaviour. Resolves the benchmark class by construction and its one dependency by lookup.
    /// </summary>
    private sealed class SingleServiceProvider(Probe probe) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            if (serviceType == typeof(Probe))
                return probe;

            return serviceType == typeof(InjectedBenchmarks) ? new InjectedBenchmarks(probe) : null;
        }
    }

}

/// <summary>The injected dependency, carrying a value the benchmark's cost depends on.</summary>
public sealed class Probe(int spins)
{
    public int Spins => spins;
}

/// <summary>
///     A benchmark class with no parameterless constructor, so it can only be measured through a
///     resolved instance. That is deliberate: were it default-constructible, a worker could fall back to
///     constructing it and the test would pass without proving the container was used.
/// </summary>
public sealed class InjectedBenchmarks(Probe probe)
{
    [Benchmark]
    [BenchmarkCategory("sp-factory")]
    public int Spin()
    {
        Thread.SpinWait(probe.Spins);

        return probe.Spins;
    }
}
