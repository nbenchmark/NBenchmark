using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     What instance lifetime a real worker measures under, and what it says about it.
/// </summary>
/// <remarks>
///     <para>
///         Both halves used to be decided in the wrong process. The lifetime was re-derived in the
///         worker from the class attribute, so a coordinator that had resolved a container-resolved
///         PerClass class down to a fresh instance per method was overruled by the attribute the
///         moment the group was isolated - which is the default Harness path. And the independence
///         warning was raised only from the coordinator's in-process path, so the run that produced
///         the sharing was the one run that never mentioned it.
///     </para>
///     <para>
///         The probe fixture throws when its second method can see the first method's state, because
///         a worker is a different process that has already exited by the time anything here is
///         asserted: the contamination itself is the only observable.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class InstanceLifetimeIsolationTests : IDisposable
{
    private const string ProbeClass = "InstanceSharingProbeBenchmarks";

    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public InstanceLifetimeIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SingleModeGuidance.ResetForTesting();
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

    private static RunGroupPayload Request(InstanceLifetime? overrideLifetime) => new()
    {
        GroupId = "instance-lifetime-group",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        DeclaringTypeFullName = IsolationFixtureLocator.ClassFullName(ProbeClass),
        BenchmarkNames = ["First", "Second"],
        Options = FastOptions,
        Order = RunOrder.Declaration,
        DisplayPrefix = ProbeClass,

        // PerMethod, deliberately: the fixture carries [InstanceLifetime(PerClass)], so a worker
        // reading the class attribute rather than the request would share the instance regardless.
        DefaultInstanceLifetime = InstanceLifetime.PerMethod,
        InstanceLifetimeOverride = overrideLifetime,
        TotalBenchmarks = 2,
    };

    private static Task<WorkerGroupRunner.GroupResult> RunAsync(RunGroupPayload request)
        => WorkerLauncher.Current.RunGroupAsync(
            request,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

    /// <summary>
    ///     The coordinator's resolved lifetime beats the class attribute in the worker.
    /// </summary>
    /// <remarks>
    ///     Without the override the fixture's own <c>[InstanceLifetime(PerClass)]</c> wins and the
    ///     second method sees the first's state - which is exactly what a scoped-DI class was doing
    ///     on the default Harness path while the coordinator believed it had resolved the lifetime
    ///     down to per-method.
    /// </remarks>
    [Fact]
    public async Task The_Resolved_Lifetime_Overrides_The_Class_Attribute()
    {
        var group = await RunAsync(Request(InstanceLifetime.PerMethod));

        Assert.True(group.Faults.Count == 0, string.Join(" | ", group.Faults.Select(f => f.Message)));
        Assert.Equal(2, group.Results.Count);
        Assert.All(group.Results, r => Assert.False(r.Errored, r.ErrorMessage));
    }

    /// <summary>
    ///     And the same request without the override shares the instance, which is what makes the
    ///     test above evidence rather than a fixture that could never have failed.
    /// </summary>
    [Fact]
    public async Task Without_The_Override_The_Class_Attribute_Shares_The_Instance()
    {
        var group = await RunAsync(Request(null));

        var errored = group.Results.Where(r => r.Errored).ToList();

        Assert.NotEmpty(errored);
        Assert.All(errored, r => Assert.Contains("shared one instance", r.ErrorMessage ?? "", StringComparison.Ordinal));
    }

    /// <summary>
    ///     W-28: a shared instance carries the independence warning back from the worker.
    /// </summary>
    /// <remarks>
    ///     The largest hole in the set, and the one the transcript that prompted this work missed
    ///     entirely: <c>ApplyPerClassIndependenceWarning</c> was called only from the coordinator's
    ///     in-process path, so a default Harness run - which measures every group in a worker -
    ///     shared the instance and said nothing at all.
    /// </remarks>
    [Fact]
    public async Task A_Shared_Instance_Warns_From_Inside_The_Worker()
    {
        var group = await RunAsync(Request(InstanceLifetime.PerClass));

        Assert.NotEmpty(group.Results);

        Assert.All(group.Results, r =>
            Assert.Contains(r.Warnings, w => w.Contains("InstanceLifetime.PerClass", StringComparison.Ordinal)
                                             && w.Contains("[SharedState]", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     A fresh instance per method has nothing to warn about.
    /// </summary>
    [Fact]
    public async Task A_PerMethod_Group_Carries_No_Independence_Warning()
    {
        var group = await RunAsync(Request(InstanceLifetime.PerMethod));

        Assert.All(group.Results, r =>
            Assert.DoesNotContain(r.Warnings, w => w.Contains("statistical-independence", StringComparison.Ordinal)));
    }
}
