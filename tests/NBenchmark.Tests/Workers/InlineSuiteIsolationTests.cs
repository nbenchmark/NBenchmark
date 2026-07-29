using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     An ordinary inline suite - no factory, no attribute, no change to how it is written - measured
///     in a worker.
///     <para>
///         This is the ergonomics guarantee. Requiring a <c>[BenchmarkPlan]</c> factory to get
///         accurate numbers would make the accurate path the inconvenient one, and people would
///         reasonably keep writing the convenient, quietly-wrong one. A plan is the escape hatch for
///         suites holding live objects a worker would have to be given rather than able to build.
///     </para>
/// </summary>
[Collection(nameof(RealWorkerCollection))]
public sealed class InlineSuiteIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public InlineSuiteIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static BenchmarkSuite Fast(BenchmarkSuite suite) => suite
        .WithIterations(16)
        .WithWarmup(1)
        .WithOpsPerSample(1)
        .WithAutoTune(AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        });

    /// <summary>
    ///     The plain shape people already write is now isolated. Nothing about the call moved.
    /// </summary>
    [Fact]
    public async Task InlineSuite_IsMeasuredInAWorker_WithNoCeremony()
    {
        var results = await Fast(new BenchmarkSuite("inline")
                .Add("fast", () => Thread.SpinWait(200))
                .Add("slow", () => Thread.SpinWait(2_000))
                .WithBaseline("fast"))
            .RunAsync();

        Assert.Equal(2, results.Count);

        foreach (var result in results)
        {
            Assert.False(result.Errored, result.ErrorMessage);
            Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
            Assert.Equal("steady-state", result.RuntimeProfileName);
            Assert.NotEmpty(result.RawSamples);
        }

        var fast = results.Single(r => r.Name == "fast");
        var slow = results.Single(r => r.Name == "slow");

        Assert.True(fast.IsBaseline);
        Assert.False(slow.IsBaseline);

        Assert.True(
            slow.Median > fast.Median * 2,
            $"expected slow to be clearly slower: fast={fast.Median:F1}ns slow={slow.Median:F1}ns");
    }

    /// <summary>
    ///     All the suite's benchmarks share one worker, so every ratio between them is a paired,
    ///     within-process comparison. Measuring each in its own process would turn every ratio into a
    ///     between-process contrast and inflate its variance for no accuracy gain - the dominant error
    ///     is the runtime configuration, which is per-process and identical here either way.
    /// </summary>
    [Fact]
    public async Task InlineSuite_MeasuresAllBenchmarksInOneWorker()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        var results = await Fast(new BenchmarkSuite("one-worker")
                .Add("a", () => Thread.SpinWait(200))
                .Add("b", () => Thread.SpinWait(200))
                .Add("c", () => Thread.SpinWait(200)))
            .RunAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal(1, launcher.GroupsRun);
        Assert.Equal(3, launcher.LastBodyCount);
    }

    /// <summary>
    ///     <c>LaunchCount</c> is the replicate count, and each replicate is a fresh worker - which is
    ///     what produces a between-process reproducibility estimate rather than repeated measurements
    ///     inside one process.
    /// </summary>
    [Fact]
    public async Task InlineSuite_LaunchCount_SpawnsOneWorkerPerReplicate()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        var results = await Fast(new BenchmarkSuite("replicated")
                .Add("a", () => Thread.SpinWait(200)))
            .WithLaunchCount(3)
            .RunAsync();

        Assert.Equal(3, launcher.GroupsRun);

        var result = Assert.Single(results);
        Assert.NotNull(result.LaunchStatistics);
        Assert.Equal(3, result.LaunchStatistics!.LaunchCount);
    }

    /// <summary>
    ///     A capturing body names itself in the refusal. A suite has several benchmarks and only one
    ///     of them is usually the problem, so a message that does not say which is not actionable.
    /// </summary>
    [Fact]
    public async Task InlineSuite_CapturingBody_NamesTheBenchmarkAndSuggestsThePlan()
    {
        var spins = 200;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("captures")
                    .Add("clean", () => Thread.SpinWait(200))
                    .Add("dirty", () => Thread.SpinWait(spins)))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        var message = stderr.ToString();
        Assert.Contains("dirty", message);
        Assert.Contains("captures", message);
        Assert.Contains("[BenchmarkPlan]", message);
    }

    /// <summary>
    ///     Suite setup and teardown are delegates in this process, so a bare-body group would run
    ///     them on the wrong side of the boundary - preparing state the benchmarks never see. The
    ///     refusal points at the plan factory, which is exactly the case plans exist for.
    /// </summary>
    [Fact]
    public async Task InlineSuite_WithSuiteLifecycle_RefusesAndPointsAtThePlan()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            await Fast(new BenchmarkSuite("lifecycle")
                    .Add("a", () => Thread.SpinWait(200))
                    .WithSuiteSetup(() => { }))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        var message = stderr.ToString();
        Assert.Contains("setup or teardown", message);
        Assert.Contains("[BenchmarkPlan]", message);
    }

    /// <summary>
    ///     <c>WithIsolation(false)</c> is a deliberate request for the host process, and is reported
    ///     as such rather than as a refusal the user should act on.
    /// </summary>
    [Fact]
    public async Task InlineSuite_WithIsolationFalse_MeasuresHereWithoutComplaint()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("opted-out")
                    .Add("a", () => Thread.SpinWait(200)))
                .WithIsolation(false)
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Single(results);
        Assert.Equal(0, launcher.GroupsRun);
        Assert.DoesNotContain("Isolation:", stderr.ToString());
    }

    /// <summary>Counts worker groups so a test can assert how the work was partitioned.</summary>
    private sealed class CountingLauncher(string workerAssemblyPath) : IWorkerLauncher
    {
        private readonly RealWorkerLauncher _inner = new(workerAssemblyPath);

        public int GroupsRun { get; private set; }
        public int LastBodyCount { get; private set; }

        public bool IsAvailable => true;

        public Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
            RunGroupPayload request,
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            GroupsRun++;
            LastBodyCount = request.Bodies.Count;

            return _inner.RunGroupAsync(request, progress, observer, timeout, cancellationToken);
        }
    }
}
