using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Benchmark instances obtained in a real worker from a container or factory the worker built
///     itself.
/// </summary>
/// <remarks>
///     <para>
///         Every one of these shapes used to be measured in the host process. The coordinator held the
///         resolver as live code, and the only thing that lifted the refusal was a
///         <c>Func&lt;IServiceProvider&gt;</c> passed to one specific overload - so
///         <c>WithInstanceFactory</c>, <c>WithScopedServiceProvider</c> and everything the scoped-DI
///         guide teaches lost the run its isolation regardless of how the factory was written.
///     </para>
///     <para>
///         The assertions are made <b>in the worker</b>, because the coordinator is a different
///         process and cannot see how many scopes were created or which constructor ran. The fixtures
///         are shaped so the wrong behaviour throws: a scoped service that can be claimed once, and a
///         benchmark class with no parameterless constructor.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class InstanceSourceIsolationTests : IDisposable
{
    private const string FixtureNamespace = "NBenchmark.Tests.IsolationFixture";

    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public InstanceSourceIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
    {
        Iterations = 4,
        WarmupIterations = 0,
        OpsPerSample = 1,
        AutoTune = AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        },
    };

    private static InstanceSourcePayload Source(InstanceSourceKind kind, string declaringType, string method) =>
        new()
        {
            Kind = kind,
            Factory = new AddressedFactory
            {
                Role = InstanceSource.RoleFor(kind),
                DeclaringTypeFullName = $"{FixtureNamespace}.{declaringType}",
                MethodName = method,
            },
        };

    private static RunGroupPayload Request(
        string benchmarkClass,
        InstanceSourcePayload source,
        InstanceLifetime lifetime,
        params string[] benchmarks) => new()
    {
        GroupId = "instance-source-group",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        DeclaringTypeFullName = IsolationFixtureLocator.ClassFullName(benchmarkClass),
        BenchmarkNames = benchmarks,
        Options = FastOptions,
        DefaultInstanceLifetime = lifetime,
        InstanceSource = source,
        TotalBenchmarks = benchmarks.Length,
    };

    private static Task<WorkerGroupRunner.GroupResult> RunAsync(RunGroupPayload request)
        => WorkerLauncher.Current.RunGroupAsync(
            request,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

    /// <summary>
    ///     W-04: a scoped container is rebuilt in the worker and each benchmark instance gets its own
    ///     scope. This is the EF Core case, and until now it could not be isolated at all.
    /// </summary>
    [Fact]
    public async Task ScopedServiceProvider_Isolates_AndScopesPerInstance()
    {
        var group = await RunAsync(Request(
            "ScopedDiBenchmarks",
            Source(InstanceSourceKind.ScopedServiceProvider, "ScopedDiFixture", "BuildServices"),
            InstanceLifetime.PerMethod,
            "First",
            "Second"));

        Assert.True(group.Faults.Count == 0, string.Join(" | ", group.Faults.Select(f => f.Message)));
        Assert.Equal(2, group.Results.Count);

        // Both succeeded, so neither claimed a scope the other had already claimed.
        Assert.All(group.Results, r => Assert.False(r.Errored, r.ErrorMessage));
    }

    /// <summary>
    ///     The same fixture resolved from the <b>root</b> fails, which is what makes the previous test
    ///     evidence rather than a coincidence.
    /// </summary>
    /// <remarks>
    ///     A scoped registration resolved off the root container behaves like a singleton, so both
    ///     benchmark instances receive the same service - the shared state that makes two methods'
    ///     timings dependent, and the reason the scoped kind has to be a distinct kind rather than a
    ///     detail the worker could infer from the factory's return type.
    /// </remarks>
    [Fact]
    public async Task UnscopedServiceProvider_Shares_The_Scoped_Service()
    {
        var group = await RunAsync(Request(
            "ScopedDiBenchmarks",
            Source(InstanceSourceKind.ServiceProvider, "ScopedDiFixture", "BuildServices"),
            InstanceLifetime.PerMethod,
            "First",
            "Second"));

        var errored = group.Results.Where(r => r.Errored).ToList();

        Assert.NotEmpty(errored);
        Assert.All(errored, r => Assert.Contains("same scope", r.ErrorMessage ?? ""));
    }

    /// <summary>
    ///     W-05: an addressed <c>Func&lt;Type, object&gt;</c> is run in the worker.
    /// </summary>
    /// <remarks>
    ///     The benchmark class has no parameterless constructor, so a worker that had fallen back to
    ///     <c>Activator.CreateInstance</c> - the silent substitution the design refuses - would fault
    ///     the group instead of measuring. A successful row is proof the caller's own factory ran in
    ///     the process that measured.
    /// </remarks>
    [Fact]
    public async Task InstanceFactory_Isolates_AndBuildsTheInstanceInTheWorker()
    {
        var group = await RunAsync(Request(
            "FactoryBuiltBenchmarks",
            Source(InstanceSourceKind.InstanceFactory, "InstanceFactoryFixture", "Create"),
            InstanceLifetime.PerMethod,
            "Measure"));

        Assert.Empty(group.Faults);

        var result = Assert.Single(group.Results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.True(result.Median > 0);
    }

    /// <summary>
    ///     A factory that cannot be located faults the group before anything is measured, rather than
    ///     surfacing inside the first instantiation.
    /// </summary>
    [Fact]
    public async Task An_Unlocatable_InstanceFactory_Faults_The_Group()
    {
        var group = await RunAsync(Request(
            "FactoryBuiltBenchmarks",
            Source(InstanceSourceKind.InstanceFactory, "InstanceFactoryFixture", "NoSuchFactory"),
            InstanceLifetime.PerMethod,
            "Measure"));

        var fault = Assert.Single(group.Faults);

        Assert.Contains("NoSuchFactory", fault.Message);
        Assert.Contains("instance factory", fault.Message);
    }
}
