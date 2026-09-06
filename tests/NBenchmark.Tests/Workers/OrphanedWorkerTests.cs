using System.Diagnostics;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     What a real <c>nbworker</c> does when its coordinator dies while a group is being measured.
/// </summary>
/// <remarks>
///     <para>
///         The protocol's orphan-avoidance claim was that the worker blocks reading its inbound pipe,
///         so a coordinator that dies closes the write end and the worker exits on its own "measured at
///         7 ms". That was true only while the worker was <i>idle</i>. Its dispatch loop awaited each
///         group before reading again, so during a group - which is essentially the whole of a run -
///         nothing read the pipe: an orphaned worker measured the entire remaining group, plus a
///         calibration it could never report, for nobody. Nothing cancelled its measurement token
///         either, so none of the loop's cancellation checks could fire.
///     </para>
///     <para>
///         These tests drive the channel directly rather than going through
///         <c>WorkerGroupRunner</c>, because that class's idle and ceiling timeouts would end the group
///         on their own and the test would pass without the worker having noticed anything.
///     </para>
/// </remarks>
public sealed class OrphanedWorkerTests
{
    /// <summary>
    ///     Long enough that a worker running it to completion cannot possibly beat the exit deadline
    ///     below: ~1200 samples at 25 ms of sleep each is roughly half a minute of work, against an
    ///     8-second allowance for noticing and exiting.
    /// </summary>
    private static RunGroupPayload LongGroup(bool measureCalibration) => new()
    {
        GroupId = "orphan-group",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        DeclaringTypeFullName = IsolationFixtureLocator.ClassFullName("LongGroupBenchmarks"),
        BenchmarkNames = ["Tick"],
        TotalBenchmarks = 1,
        MeasureCalibration = measureCalibration,
        Options = MeasurementOptions.Default with
        {
            Samples = 1_200,
            WarmupSamples = 1,
            OpsPerSample = 1,
            AutoTune = AutoTuneOptions.Default with
            {
                MaxTuningTime = TimeSpan.FromMinutes(5),
                MinWarmupTime = TimeSpan.Zero,
                MinMeasurementTime = TimeSpan.Zero,
                RequireJitQuiescence = false,
                EnableJitterCalibration = false,
            },
        },
    };

    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(8);

    /// <summary>
    ///     Reads frames until the worker is provably measuring, so the pipe is closed on a worker in
    ///     the middle of a group rather than on an idle one. The idle case already has coverage and is
    ///     not the defect.
    /// </summary>
    private static async Task WaitUntilMeasuringAsync(WorkerHost worker)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        while (true)
        {
            var frame = await worker.Channel.ReadAsync(cts.Token);

            Assert.NotNull(frame);

            if (frame.Kind is WorkerFrameKind.Progress or WorkerFrameKind.ObserverPhase)
                return;

            Assert.NotEqual(WorkerFrameKind.GroupCompleted, frame.Kind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_WhoseCoordinatorDiesMidGroup_StopsMeasuringAndExitsOnItsOwn(bool measureCalibration)
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        await worker.Channel.WriteAsync(
            WorkerFrame.Of(LongGroup(measureCalibration)), CancellationToken.None);

        await WaitUntilMeasuringAsync(worker);

        var stopwatch = Stopwatch.StartNew();

        worker.Abandon();

        await worker.WaitForExitAsync(ExitDeadline);

        stopwatch.Stop();

        // The exit code asserts the *mechanism*, not just the outcome: CoordinatorLost means the worker
        // noticed end-of-stream and chose to stop. "exit code 0" would mean it finished the group,
        // "killed by signal N" that something else ended it, and "the process is still running" that it
        // never noticed - each a different failure, and the message names which.
        Assert.Equal($"exit code {WorkerExitCode.CoordinatorLost}", worker.ExitDescription);

        // The group is ~30 s of sleeping, plus a calibration pass when one was requested. Beating the
        // deadline is therefore only possible by having abandoned the measurement partway through.
        Assert.True(
            stopwatch.Elapsed < ExitDeadline,
            $"worker took {stopwatch.Elapsed} to exit, which is long enough that it may have run the "
            + "group to completion rather than stopping when the coordinator went away");
    }

    /// <summary>
    ///     The control, and the one that fails if the cancellation is wired too aggressively: a group
    ///     whose coordinator is alive still runs to completion and still reports its results. A
    ///     <c>FrameQueue</c> that treated any <see cref="OperationCanceledException" /> as coordinator
    ///     loss, or a pump that cancelled on a benign shutdown, would break exactly here.
    /// </summary>
    [Fact]
    public async Task Worker_ThatFinishesAGroupWithALiveCoordinator_StillReportsIt()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var request = LongGroup(measureCalibration: false) with
        {
            Options = LongGroup(measureCalibration: false).Options with { Samples = 4 },
        };

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            request,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Empty(group.Faults);

        var result = Assert.Single(group.Results);

        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
        Assert.NotEmpty(group.RawSamples[result.Name]);
    }
}
