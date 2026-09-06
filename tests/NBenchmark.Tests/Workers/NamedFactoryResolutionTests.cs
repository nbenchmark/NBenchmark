using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Resolving an <see cref="AddressedFactory" /> by <b>name</b> in a real worker.
/// </summary>
/// <remarks>
///     <para>
///         Name addressing is what a multi-runtime run uses: each runtime's assembly is a separate
///         build, in which a metadata token from the coordinator's build identifies nothing, and the
///         module version id that normally guards a stale token differs between builds by
///         construction. A fully-qualified name is the only stable address across them.
///     </para>
///     <para>
///         It had no automated coverage. The only thing exercising it was
///         <c>samples/MultiRuntimeSuite</c>, run by hand - so the resolver could be rewritten, pass
///         every test, and break every multi-runtime run. These tests address the fixture's plan by
///         name against the <i>same</i> build, which exercises the resolution mechanism without
///         paying for three <c>dotnet build</c> invocations.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class NamedFactoryResolutionTests : IDisposable
{
    private const string FixtureType = "NBenchmark.Tests.IsolationFixture.NamedPlanFixture";

    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public NamedFactoryResolutionTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SingleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static RunGroupPayload PlanRequest(string methodName) => new()
    {
        GroupId = "named-plan-group",
        Kind = WorkGroupKind.Plan,
        TargetAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        Plan = new AddressedFactory
        {
            Role = "the benchmark plan",
            DeclaringTypeFullName = FixtureType,
            MethodName = methodName,
        },
        Options = MeasurementOptions.Default with { Samples = 8, WarmupSamples = 1, OpsPerSample = 1 },
        TotalBenchmarks = 1,
    };

    private static Task<WorkerGroupRunner.GroupResult> RunAsync(RunGroupPayload request)
        => WorkerLauncher.Current.RunGroupAsync(
            request,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

    /// <summary>
    ///     The mechanism, end to end: a factory named only by type and method is located in the
    ///     target assembly, invoked, and its suite measured.
    /// </summary>
    [Fact]
    public async Task A_Plan_Addressed_By_Name_Is_Located_And_Measured()
    {
        var group = await RunAsync(PlanRequest(nameof(NamedPlanFixtureNames.BuildSuite)));

        Assert.Empty(group.Faults);
        Assert.False(group.WorkerDied);

        var result = Assert.Single(group.Results);

        Assert.Equal(NamedPlanFixtureNames.BenchmarkName, result.Name);
        Assert.False(result.Errored);
        Assert.True(result.MedianNs > 0);
    }

    /// <summary>
    ///     A name that resolves to nothing faults the group and says which name it looked for,
    ///     rather than surfacing as a worker that produced no results.
    /// </summary>
    [Fact]
    public async Task A_Missing_Method_Faults_And_Names_What_Was_Looked_For()
    {
        var group = await RunAsync(PlanRequest("NoSuchMethod"));

        var fault = Assert.Single(group.Faults);

        Assert.Contains("NoSuchMethod", fault.Message);
        Assert.Contains("static parameterless method", fault.Message);
        Assert.Empty(group.Results);
    }

    /// <summary>
    ///     The return-type check runs against the build that will actually execute the method, which
    ///     is the point of checking it in the worker rather than trusting the coordinator: under
    ///     another target framework the method genuinely might have a different shape.
    /// </summary>
    [Fact]
    public async Task A_Method_Returning_The_Wrong_Type_Faults_Before_It_Is_Invoked()
    {
        var group = await RunAsync(PlanRequest(nameof(NamedPlanFixtureNames.NotASuite)));

        var fault = Assert.Single(group.Faults);

        Assert.Contains("rather than", fault.Message);
        Assert.Contains(nameof(BenchmarkSuite), fault.Message);
        Assert.Empty(group.Results);
    }

    /// <summary>
    ///     A11: the fixture executable only <i>references</i>
    ///     <c>NBenchmark.Tests.SharedPlanFixture</c> - the plan factory is not declared in it. By-name
    ///     addressing exists for multi-runtime, where sharing one plan factory across the per-runtime
    ///     projects is exactly the point, so a factory living one level away in the target's own
    ///     dependency graph is the ordinary shape, not an edge case. Before this, only the target
    ///     assembly itself was searched, so a factory placed here for exactly that reason was refused
    ///     as "not found" in the one assembly that was never going to declare it.
    /// </summary>
    [Fact]
    public async Task A_Plan_Declared_In_A_Referenced_Library_Is_Located_And_Measured()
    {
        var request = PlanRequest("BuildSuite") with
        {
            Plan = new AddressedFactory
            {
                Role = "the benchmark plan",
                DeclaringTypeFullName = "NBenchmark.Tests.SharedPlanFixture.SharedHelperPlan",
                MethodName = "BuildSuite",
            },
        };

        var group = await RunAsync(request);

        Assert.Empty(group.Faults);
        Assert.False(group.WorkerDied);

        var result = Assert.Single(group.Results);

        Assert.Equal("only", result.Name);
        Assert.False(result.Errored);
    }

    /// <summary>
    ///     A missing declaring type is named too. The fixture assembly loads fine, so this is the type
    ///     lookup failing rather than the assembly, and the two need different fixes.
    /// </summary>
    [Fact]
    public async Task A_Missing_Declaring_Type_Faults_And_Names_The_Type()
    {
        var request = PlanRequest("BuildSuite");

        request = request with
        {
            Plan = request.Plan! with { DeclaringTypeFullName = "NBenchmark.Tests.IsolationFixture.NoSuchType" },
        };

        var fault = Assert.Single((await RunAsync(request)).Faults);

        Assert.Contains("NoSuchType", fault.Message);
        Assert.Contains("was not found", fault.Message);
    }

    /// <summary>
    ///     Mirrors the fixture's member names, which live in a separate assembly this one does not
    ///     reference. Kept as constants rather than string literals at each call site so a rename in
    ///     the fixture fails in one place.
    /// </summary>
    private static class NamedPlanFixtureNames
    {
        public const string BuildSuite = nameof(BuildSuite);

        public const string NotASuite = nameof(NotASuite);

        public const string BenchmarkName = "only";
    }
}
