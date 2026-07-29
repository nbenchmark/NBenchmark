using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     End-to-end tests against a real <c>nbworker</c> process measuring a real benchmark assembly.
///     <para>
///         These are the tests that make the worker design trustworthy rather than plausible. The
///         defect that emptied <see cref="BenchmarkResult.RawSamples" /> on every isolated result -
///         and so disabled significance testing in the default mode - survived because every
///         isolation test in the repo substituted a fake launcher. A protocol round-trip over a
///         <see cref="MemoryStream" /> would have passed just as happily.
///     </para>
/// </summary>
public sealed class RealWorkerTests
{
    /// <summary>
    ///     Short but real: enough samples for statistics to exist, few enough that the suite stays
    ///     quick. The point of these tests is the process boundary, not measurement quality.
    /// </summary>
    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
    {
        Iterations = 24,
        WarmupIterations = 2,
        OpsPerSample = 1,
        LaunchCount = 1,
        AutoTune = AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        },
    };

    private static RunGroupPayload DiscoveredGroup(
        string className,
        RuntimeProfile profile,
        params string[] benchmarks) => new()
    {
        GroupId = "test-group",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        DeclaringTypeFullName = IsolationFixtureLocator.ClassFullName(className),
        BenchmarkNames = benchmarks,
        Options = FastOptions with { RuntimeProfile = profile },
        TotalBenchmarks = benchmarks.Length,
    };

    /// <summary>
    ///     The whole point of the process boundary, asserted directly: the worker reports the runtime
    ///     configuration the coordinator asked for, because the coordinator applied it to the
    ///     worker's environment block before the process started. There is no other moment at which
    ///     this could have been done.
    /// </summary>
    [Theory]
    [InlineData("steady-state", "tiered=off pgo=off r2r=off")]
    [InlineData("production", "tiered=on pgo=on r2r=on")]
    public async Task Worker_ReportsTheRuntimeProfileItWasLaunchedUnder(string profileName, string expectedKnobs)
    {
        Assert.True(RuntimeProfile.TryParse(profileName, out var profile));

        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), profile, CancellationToken.None);

        Assert.Equal(profileName, worker.Ready.RuntimeProfileName);
        Assert.Equal(expectedKnobs, worker.Ready.RuntimeKnobs);
        Assert.True(worker.Ready.RuntimeProfileApplied);
        Assert.NotEqual(Environment.ProcessId, worker.Ready.WorkerProcessId);
        Assert.Equal(WorkerProtocol.Version, worker.Ready.ProtocolVersion);
    }

    /// <summary>
    ///     A worker launched with no profile must report <c>host</c> rather than claiming a fidelity
    ///     it does not have. Reading its own environment rather than echoing the request is what
    ///     makes that impossible to get wrong.
    /// </summary>
    [Fact]
    public async Task Worker_WithNoProfile_ReportsHost()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.Host, CancellationToken.None);

        Assert.Equal("host", worker.Ready.RuntimeProfileName);
        Assert.False(worker.Ready.RuntimeProfileApplied);
    }

    /// <summary>
    ///     The regression guard for the defect this whole area exists to prevent. Raw samples must
    ///     arrive with their result - in both instance lifetimes, because the previous design had a
    ///     separate code path for each and the same bug in both.
    /// </summary>
    [Theory]
    [InlineData("IsolationFixtureBenchmarks")]
    [InlineData("SharedInstanceBenchmarks")]
    public async Task Worker_MeasuresDiscoveredClass_AndReturnsRawSamplesWithEachResult(string className)
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DiscoveredGroup(className, RuntimeProfile.SteadyState, "Fast", "Slow"),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Empty(group.Faults);
        Assert.False(group.WorkerDied);
        Assert.Equal(2, group.Results.Count);

        foreach (var result in group.Results)
        {
            Assert.False(result.Errored, result.ErrorMessage);

            // Non-empty samples are what significance testing needs. Emptying these was the defect.
            Assert.True(
                group.RawSamples[result.Name].Length > 0,
                $"'{result.Name}' came back with no raw samples.");

            Assert.True(result.Mean > 0);

            // Stamped by the worker from its own environment, so it describes the process that
            // actually did the measuring.
            Assert.Equal("steady-state", result.RuntimeProfileName);
            Assert.Equal("tiered=off pgo=off r2r=off", result.RuntimeKnobs);
        }

        // The fixture's two bodies differ by an order of magnitude of spin count, so a worker that
        // measured them at all must separate them. This catches a worker that returned plausible
        // numbers for the wrong bodies.
        var fast = group.Results.Single(r => r.Name.EndsWith(".Fast", StringComparison.Ordinal));
        var slow = group.Results.Single(r => r.Name.EndsWith(".Slow", StringComparison.Ordinal));

        Assert.True(
            slow.Median > fast.Median * 2,
            $"expected Slow to be clearly slower: Fast={fast.Median:F1}ns Slow={slow.Median:F1}ns");
    }

    /// <summary>
    ///     One worker serves several groups. Each group is independently framed, so a second request
    ///     on a warm worker must behave exactly like the first - this is what makes pre-spawning
    ///     worth doing.
    /// </summary>
    [Fact]
    public async Task Worker_ServesSuccessiveGroups()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        for (var round = 0; round < 2; round++)
        {
            var group = await WorkerGroupRunner.RunAsync(
                worker,
                DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast") with
                {
                    GroupId = $"round-{round}",
                },
                NullBenchmarkProgress.Instance,
                NullMeasurementObserver.Instance,
                TimeSpan.FromMinutes(2),
                CancellationToken.None);

            Assert.Empty(group.Faults);
            Assert.Single(group.Results);
            Assert.True(group.RawSamples.Values.Single().Length > 0);
        }
    }

    /// <summary>
    ///     Live telemetry crosses the boundary. The previous isolated path had no channel for this at
    ///     all, so children measured silently and a progress bar simply stalled for the duration of
    ///     the real work.
    /// </summary>
    [Fact]
    public async Task Worker_StreamsProgressAndPhaseEventsBack()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var progress = new RecordingProgress();
        var observer = new RecordingObserver();

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast"),
            progress,
            observer,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Empty(group.Faults);

        // Lifecycle and phase events cross the boundary while the work is happening.
        Assert.Contains(ProgressCallback.BenchmarkStarting.ToString(), progress.Calls);
        Assert.Contains(ProgressCallback.WarmupStarting.ToString(), progress.Calls);
        Assert.Contains(MeasurementPhase.Measurement, observer.Phases);
        Assert.Contains(MeasurementPhase.Warmup, observer.Phases);

        // Completion callbacks are deliberately not raised here. A group can be measured by several
        // replicate workers, and the result a consumer should see is the aggregate across them - so
        // the caller that owns the aggregation raises them, exactly once, from the final result.
        Assert.DoesNotContain(nameof(IBenchmarkProgress.OnBenchmarkCompleted), progress.Calls);
        Assert.Empty(observer.Results);

        // The suite-completed sentinel belongs to the whole run and must not be emitted per worker.
        Assert.DoesNotContain(MeasurementPhase.SuiteCompleted, observer.Phases);
    }

    /// <summary>
    ///     A class that no longer exists must produce a fault the user can act on, not a silently
    ///     empty result set.
    /// </summary>
    [Fact]
    public async Task Worker_FaultsOnMissingClass()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DiscoveredGroup("NoSuchClass", RuntimeProfile.SteadyState, "Fast"),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        Assert.Empty(group.Results);
        var fault = Assert.Single(group.Faults);
        Assert.Contains("NoSuchClass", fault.Message);

        // A group-level fault becomes an errored row so the benchmark does not vanish from the table.
        var errored = WorkerGroupRunner.ToErroredResults(group, ["Fast"], "");
        Assert.True(Assert.Single(errored).Errored);
    }

    /// <summary>
    ///     A wedged benchmark body is bounded and reported, not left to hang the run. The previous
    ///     launcher waited on a bare <c>WaitForExitAsync</c> with no ceiling at all.
    /// </summary>
    [Fact]
    public async Task Worker_ThatWedges_IsStoppedAndReported()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DiscoveredGroup("HangingBenchmarks", RuntimeProfile.SteadyState, "Hang"),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(group.WorkerDied);
        Assert.Contains("ceiling", Assert.Single(group.Faults).Message);
    }

    /// <summary>
    ///     Orphan avoidance is structural: the worker blocks reading its inbound pipe, so closing the
    ///     coordinator's end is enough to end it. Nothing supervises the worker, which matters
    ///     because the supervisor would be the process most likely to have died.
    /// </summary>
    [Fact]
    public async Task Worker_ExitsWhenTheCoordinatorClosesThePipe()
    {
        var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var processId = worker.ProcessId;

        // DisposeAsync sends a shutdown frame and then closes the pipe; either alone is sufficient.
        await worker.DisposeAsync();

        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(processId));
    }

    private sealed class RecordingProgress : IBenchmarkProgress
    {
        public List<string> Calls { get; } = [];

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) => Record(nameof(OnSuiteStarting));

        public Task OnWarmupStarting(string name, int totalWarmupIterations)
            => Record(ProgressCallback.WarmupStarting.ToString());

        public Task OnWarmupCompleted(string name) => Record(ProgressCallback.WarmupCompleted.ToString());

        public Task OnBenchmarkStarting(string name, int index, int total)
            => Record(ProgressCallback.BenchmarkStarting.ToString());

        public Task OnIterationCompleted(string name, int iteration, int totalIterations)
            => Record(ProgressCallback.IterationCompleted.ToString());

        public Task OnBenchmarkCompleted(BenchmarkResult result) => Record(nameof(OnBenchmarkCompleted));

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Record(nameof(OnSuiteCompleted));

        private Task Record(string call)
        {
            lock (Calls)
            {
                Calls.Add(call);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     The sample cap applied across a real process boundary. A worker measures up to
    ///     <see cref="MeasurementOptions.MaxIterations" /> samples and the whole array used to cross
    ///     on every result; it now sends a bounded representative subset.
    /// </summary>
    [Fact]
    public async Task Worker_ReturnsAtMostTheConfiguredNumberOfRawSamples()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        // More samples measured than the cap allows back, so the reduction has to engage.
        var request = DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast") with
        {
            Options = FastOptions with
            {
                RuntimeProfile = RuntimeProfile.SteadyState,
                Iterations = 400,
                MaxRawSamples = 64,
            },
        };

        var group = await WorkerGroupRunner.RunAsync(
            worker, request, NullBenchmarkProgress.Instance, NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Empty(group.Faults);

        var samples = Assert.Single(group.RawSamples).Value;

        Assert.Equal(64, samples.Length);
    }

    /// <summary>
    ///     <c>--emit-raw</c>: the cap lifts and the full series crosses, for a consumer that wants to
    ///     analyse the run itself.
    /// </summary>
    [Fact]
    public async Task Worker_WithAnUnboundedCap_ReturnsEverySample()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var request = DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast") with
        {
            Options = FastOptions with
            {
                RuntimeProfile = RuntimeProfile.SteadyState,
                Iterations = 400,
                MaxRawSamples = MeasurementOptions.UnboundedRawSamples,
            },
        };

        var group = await WorkerGroupRunner.RunAsync(
            worker, request, NullBenchmarkProgress.Instance, NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Empty(group.Faults);

        var samples = Assert.Single(group.RawSamples).Value;

        Assert.Equal(400, samples.Length);
    }

    /// <summary>
    ///     The reduced array and its trim marks have to arrive consistent with each other. An ordinal
    ///     past the end of the array it indexes would be a reporter crash; one merely pointing at the
    ///     wrong sample would be worse, because it would render.
    /// </summary>
    [Fact]
    public async Task Worker_ReturnsTrimmedOrdinalsThatIndexTheSamplesItSent()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var request = DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast") with
        {
            Options = FastOptions with
            {
                RuntimeProfile = RuntimeProfile.SteadyState,
                Iterations = 400,
                MaxRawSamples = 64,
                OutlierMode = OutlierMode.IqrFence,
            },
        };

        var group = await WorkerGroupRunner.RunAsync(
            worker, request, NullBenchmarkProgress.Instance, NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Empty(group.Faults);

        var result = Assert.Single(group.Results);
        var samples = group.RawSamples[result.Name];

        foreach (var ordinal in result.TrimmedOrdinals)
            Assert.InRange(ordinal, 0, samples.Length - 1);
    }

    /// <summary>
    ///     The fix for the last cross-process comparison in the library. A test-integration gate that
    ///     ratios against the calibration standard needs its divisor measured in the same process, and
    ///     under the same runtime configuration, as the candidate - so the worker measures it.
    /// </summary>
    [Fact]
    public async Task Worker_MeasuresTheCalibrationStandardInItsOwnProcess()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var request = DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast") with
        {
            MeasureCalibration = true,
        };

        var group = await WorkerGroupRunner.RunAsync(
            worker, request, NullBenchmarkProgress.Instance, NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Empty(group.Faults);

        var calibration = Assert.IsType<CalibrationResult>(group.Calibration);

        Assert.True(calibration.Mean > 0);
        Assert.True(calibration.Median > 0);
        Assert.NotEmpty(calibration.Samples);
    }

    /// <summary>
    ///     Not measured unless asked for. It is cheap, but a gate that names a reference method has no
    ///     use for it, and work nobody consumes is still work in the middle of a measurement.
    /// </summary>
    [Fact]
    public async Task Worker_SkipsTheCalibrationWhenItWasNotRequested()
    {
        await using var worker = await WorkerHost.StartAsync(
            WorkerLocatorForTests.WorkerAssemblyPath(), RuntimeProfile.SteadyState, CancellationToken.None);

        var group = await WorkerGroupRunner.RunAsync(
            worker,
            DiscoveredGroup("IsolationFixtureBenchmarks", RuntimeProfile.SteadyState, "Fast"),
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Empty(group.Faults);
        Assert.Null(group.Calibration);
    }

    private sealed class RecordingObserver : IMeasurementObserver
    {
        public List<MeasurementPhase> Phases { get; } = [];
        public List<BenchmarkResult> Results { get; } = [];

        public void OnPhase(in MeasurementPhaseEvent e)
        {
            lock (Phases)
            {
                Phases.Add(e.Phase);
            }
        }

        public void OnSample(in SampleEvent e)
        {
        }

        public void OnDetector(in DetectorStateEvent e)
        {
        }

        public void OnResult(BenchmarkResult result)
        {
            lock (Results)
            {
                Results.Add(result);
            }
        }
    }
}
