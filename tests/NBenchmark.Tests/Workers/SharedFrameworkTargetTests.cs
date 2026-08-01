using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     End-to-end coverage for measuring a target that needs a shared framework the worker does not
///     declare - an ASP.NET Core project being the case that reported it.
/// </summary>
/// <remarks>
///     <para>
///         <c>nbworker</c> is a plain <c>Microsoft.NET.Sdk</c> application and declares
///         <c>Microsoft.NETCore.App</c> alone. A benchmark class in a <c>Microsoft.NET.Sdk.Web</c>
///         project sits in an assembly whose graph reaches <c>Microsoft.AspNetCore.App</c>, whose
///         assemblies are framework-provided: absent from the target's <c>deps.json</c>, correctly
///         unresolved by <c>AssemblyDependencyResolver</c>, and expected on a trusted-platform-assembly
///         list that a worker started without that framework does not have. The load failed with
///         <c>Could not load file or assembly 'Microsoft.Extensions.Hosting.Abstractions'</c> before
///         anything was measured.
///     </para>
///     <para>
///         These tests use a real fixture assembly with a real two-framework
///         <c>runtimeconfig.json</c>, because the failure is in what <c>hostfxr</c> does with a
///         process's framework set - which no in-process test can observe.
///     </para>
/// </remarks>
public sealed class SharedFrameworkTargetTests
{
    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
    {
        Iterations = 8,
        WarmupIterations = 1,
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

    /// <summary>
    ///     The decision itself: a target declaring a framework the worker does not gets a synthesized
    ///     config, and one declaring nothing extra does not.
    /// </summary>
    [Fact]
    public void WebFixture_NeedsAnExtendedFrameworkSet_AndTheIsolationFixtureDoesNot()
    {
        var worker = WorkerLocatorForTests.WorkerAssemblyPath();

        Assert.NotNull(SharedFrameworkConfig.ResolveFor(worker, WebFixtureLocator.AssemblyPath()));
        Assert.Null(SharedFrameworkConfig.ResolveFor(worker, IsolationFixtureLocator.AssemblyPath()));
    }

    /// <summary>
    ///     The fix, proved against a real worker process: benchmarks in an assembly that needs the
    ///     ASP.NET Core shared framework are measured, with no faults and no dead worker.
    /// </summary>
    [Fact]
    public async Task Worker_MeasuresATargetThatNeedsASharedFramework()
    {
        var workerPath = WorkerLocatorForTests.WorkerAssemblyPath();
        var runtimeConfig = SharedFrameworkConfig.ResolveFor(workerPath, WebFixtureLocator.AssemblyPath());

        Assert.NotNull(runtimeConfig);

        await using var worker = await WorkerHost.StartAsync(
            workerPath, RuntimeProfile.SteadyState, runtimeConfig, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            WebFixtureGroup(),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Empty(group.Faults);
        Assert.False(group.WorkerDied);
        Assert.Equal(2, group.Results.Count);
        Assert.All(group.Results, r => Assert.False(r.Errored, r.ErrorMessage));
    }

    /// <summary>
    ///     The control. Without the synthesized config the same group fails - which is what makes the
    ///     test above evidence of the fix rather than evidence that ASP.NET Core happens to be
    ///     installed on the machine running it.
    /// </summary>
    /// <remarks>
    ///     Asserted as "produced no measurement", not as a particular message or a particular failure
    ///     shape. The failure comes out of the runtime's assembly resolver, whose wording is not ours,
    ///     and lands as a group fault or an errored row depending on whether the missing type is
    ///     reached while loading the class or while running a body.
    /// </remarks>
    [Fact]
    public async Task Worker_WithoutTheExtendedFrameworkSet_CannotLoadTheTarget()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(),
            RuntimeProfile.SteadyState,
            runtimeConfigPath: null,
            CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            WebFixtureGroup(),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.True(
            group.Faults.Count > 0 || group.Results.All(r => r.Errored),
            "A worker without the ASP.NET Core shared framework measured a target that needs it, so "
            + "this fixture no longer reproduces the defect and the test above proves nothing.");
    }

    /// <summary>
    ///     The same thing through the production entry point, which is where the decision is actually
    ///     made. The two tests above prove the pieces work; this one proves they are wired together -
    ///     <see cref="WorkerLauncher" /> is the single chokepoint every mode reaches a worker through,
    ///     and a fix that never gets asked for there is not a fix.
    /// </summary>
    /// <remarks>
    ///     No launcher substitution: <see cref="WorkerLauncher.Current" /> is the real
    ///     process-spawning implementation by default, and the request names the worker explicitly, so
    ///     nothing depends on where a worker happens to be deployed relative to a test host.
    /// </remarks>
    [Fact]
    public async Task Launcher_ResolvesTheFrameworkSetItself()
    {
        var group = await WorkerLauncher.Current.RunGroupAsync(
            WebFixtureGroup() with { WorkerAssemblyPath = WorkerLocatorForTests.WorkerAssemblyPath() },
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Empty(group.Faults);
        Assert.False(group.WorkerDied);
        Assert.Equal(2, group.Results.Count);
        Assert.All(group.Results, r => Assert.False(r.Errored, r.ErrorMessage));
    }

    private static RunGroupPayload WebFixtureGroup() => new()
    {
        GroupId = "web-fixture-group",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = WebFixtureLocator.AssemblyPath(),
        DeclaringTypeFullName = WebFixtureLocator.ClassFullName("WebFixtureBenchmarks"),
        BenchmarkNames = ["IsGet", "HasPath"],
        Options = FastOptions with { RuntimeProfile = RuntimeProfile.SteadyState },
        TotalBenchmarks = 2,
    };
}
