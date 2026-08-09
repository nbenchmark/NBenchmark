using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Crash resilience against a worker that dies hard mid-group, end to end against a real
///     <c>nbworker</c> process.
/// </summary>
/// <remarks>
///     <para>
///         These are the tests the plan (W-49) says did not exist: nothing covered a worker that
///         stack-overflows, <c>FailFast</c>s, or <c>Environment.Exit</c>s, so nothing asserted the crash
///         message text, nothing asserted stderr is surfaced, and <c>FrameChannelTests</c> had no
///         torn-frame test. The fixtures live in <c>NBenchmark.Tests.IsolationFixture</c> alongside the
///         other real-child-process fixtures, because the worker re-runs the entry assembly and under
///         <c>dotnet test</c> that is the test host.
///     </para>
///     <para>
///         They tie the whole of Phase 5 together: an already-sent result survives the crash (W-44),
///         the torn-frame or clean-end death is a fault not an unhandled exception (W-45), a
///         surviving row carries a warning that nothing it sent can be trusted (W-46), the fault names
///         the exit cause rather than printing a bare number (W-47), and the worker's own stderr
///         reaches the user (W-48). The <c>FrameChannel</c>-level torn-frame tests cover W-51
///         separately and deterministically.
///     </para>
/// </remarks>
public sealed class HardCrashTests
{
    // The marker line the HardExitBenchmarks.Crash body writes to stderr before exiting. Duplicated
    // here because the fixture is a separate assembly the test loads by path at runtime rather
    // than references at compile time, so its constants are not visible here.
    private const string StderrMarker = "hard-exit fixture: crashing the worker with exit code 70";

    /// <summary>
    ///     Short but real, matching the other real-worker suites: enough samples for statistics to
    ///     exist, few enough that the suite stays quick. The point of these tests is the crash, not
    ///     measurement quality.
    /// </summary>
    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
    {
        Iterations = 24,
        WarmupIterations = 2,
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
    ///     A discovered-class group in declaration order, so <c>Safe</c> is measured before the
    ///     crashing method and its result is on the wire before the worker dies.
    /// </summary>
    private static RunGroupPayload DeclaredOrderGroup(
        string className,
        RuntimeProfile profile,
        params string[] benchmarks) => new()
    {
        GroupId = "crash-group",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        DeclaringTypeFullName = IsolationFixtureLocator.ClassFullName(className),
        BenchmarkNames = benchmarks,
        // Declaration order is what makes the crash deterministic: the safe benchmark is declared
        // first, so it is measured and (once sends are incremental) reported before the crashing one
        // runs. Random order would race the two and a passing test would prove nothing about survival.
        Order = RunOrder.Declaration,
        Options = FastOptions with { RuntimeProfile = profile },
        TotalBenchmarks = benchmarks.Length,
    };

    /// <summary>
    ///     The worker exits mid-group with a fixed code and a stderr marker, after one benchmark has
    ///     already completed. That already-sent result must survive (W-44); the death must be a fault,
    ///     not an unhandled <see cref="EndOfStreamException" /> (W-45); the surviving row must carry a
    ///     warning (W-46); the fault must name the exit cause (W-47); and the worker's stderr must reach
    ///     the user (W-48).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The result is awaited directly rather than through <c>Assert.Throws</c>: the assertion
    ///         is that <see cref="WorkerGroupRunner.RunAsync" /> <i>returns</i> a faulted group rather
    ///         than throwing. Before W-45, a torn frame escaped as an unhandled
    ///         <see cref="EndOfStreamException" /> and the await itself blew up.
    ///     </para>
    ///     <para>
    ///         <see cref="RunOrder.Declaration" /> guarantees <c>Safe</c> runs first; the W-44
    ///         incremental send then puts its result on the wire before <c>Crash</c> exits, so the
    ///         result is already in the coordinator's hands when the worker dies. Before W-44 the
    ///         worker batched results to the end of the group, so a crash on the last benchmark
    ///         annihilated every result - the amplifier the plan opens Phase 5 with.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task HardExit_KeepsAlreadySentResult_FaultsNamesTheCauseAndSurfacesStderr()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DeclaredOrderGroup("HardExitBenchmarks", RuntimeProfile.SteadyState, "Safe", "Crash"),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        // W-45: the death is a faulted group, not an unhandled exception that ended the run.
        Assert.True(group.WorkerDied);
        Assert.NotEmpty(group.Faults);

        // W-44: the benchmark that finished before the crash was already on the wire, so its result
        // survives. Before the incremental send this was empty and the crash lost everything.
        var survived = Assert.Single(group.Results);
        Assert.EndsWith("Safe", survived.Name);

        // W-46: a result the worker managed to send is not trustworthy, because the worker died before
        // the group finished - its own contract says nothing it sent can be assumed complete. The
        // warning is how a consumer's report distinguishes a measured row from a row measured by a
        // process that then died.
        Assert.NotEmpty(survived.Warnings);

        var fault = Assert.Single(group.Faults);

        // W-47: the fault names the exit cause rather than printing a bare number. Exit code 70 is the
        // worker's own "crashed" code, so the description is "exit code 70" - not the old, inverted
        // "killed by signal ..." rendering.
        Assert.Contains("exit code 70", fault.Message);

        // W-48: the stderr the worker produced before it died reaches the user, so the fault is not
        // just "it vanished". The fixture writes a marker line to stderr and flushes it before exit.
        Assert.Contains(StderrMarker, fault.Message);
    }

    /// <summary>
    ///     A stack overflow is the reachable torn-frame case: the worker dies hard while writing, and
    ///     whether the coordinator reads a truncated frame (<c>EndOfStreamException</c>) or a clean end
    ///     (<c>null</c>) depends on timing. Both paths must produce a fault rather than an unhandled
    ///     exception that takes down the whole benchmark program.
    /// </summary>
    /// <remarks>
    ///     The assertion is deliberately just "the run did not terminate with an unhandled
    ///     <see cref="EndOfStreamException" />" - i.e. <see cref="WorkerGroupRunner.RunAsync" />
    ///     returned a faulted group. The exact exit code for a stack overflow is platform- and
    ///     runtime-specific, so the test does not pin it; the W-47 unit tests cover the code-to-cause
    ///     table deterministically.
    /// </remarks>
    [Fact]
    public async Task StackOverflow_FaultsTheGroupNotTheRun()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DeclaredOrderGroup("StackOverflowBenchmarks", RuntimeProfile.SteadyState, "Overflow"),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.True(group.WorkerDied);
        Assert.NotEmpty(group.Faults);
    }
}