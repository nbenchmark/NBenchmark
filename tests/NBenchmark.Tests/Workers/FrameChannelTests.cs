using NBenchmark.Stats;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Transport and payload-fidelity tests for the coordinator/worker protocol. These run over
///     a real anonymous pipe pair rather than a <see cref="MemoryStream" />, because the two
///     properties that matter most - that a partial read is looped rather than treated as a short
///     frame, and that end-of-stream is reported as <c>null</c> rather than throwing - only exist
///     on a real pipe.
/// </summary>
public sealed class FrameChannelTests
{
    private static (FrameChannel Left, FrameChannel Right, IDisposable Cleanup) CreatePair()
        => FramePipePair.Create();

    [Fact]
    public async Task Handshake_RoundTrips()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new HandshakePayload { ProtocolVersion = 7, ParentProcessId = 4242 }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(WorkerFrameKind.Handshake, frame.Kind);
        Assert.Equal(7, frame.Handshake!.ProtocolVersion);
        Assert.Equal(4242, frame.Handshake.ParentProcessId);
    }

    /// <summary>
    ///     The frame the worker exists to deliver. Every field is gated on or stamped onto results by
    ///     <c>WorkerHost.HandshakeAsync</c>: a version mismatch <i>refuses to start the worker</i>, and
    ///     the profile name and knobs go onto every result the worker produces. So a member that fails
    ///     to cross here surfaces either as a bogus "stale nbworker on disk" error or - worse - as a
    ///     measurement labelled with a runtime configuration it did not run under.
    /// </summary>
    [Fact]
    public async Task Ready_RoundTripsEveryFieldTheHandshakeGatesOn()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ReadyPayload
            {
                ProtocolVersion = 11,
                WorkerProcessId = 31337,
                RuntimeProfileName = "steady-state",
                RuntimeKnobs = "tiered=off pgo=off r2r=off concurrentGc=off",
                RuntimeProfileApplied = true,
                TargetFramework = "net10.0",
                EngineVersion = "1.2.3-preview.4",
                ProcessArchitecture = "Arm64",
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Equal(WorkerFrameKind.Ready, frame!.Kind);

        var received = frame.Ready!;

        Assert.Equal(11, received.ProtocolVersion);
        Assert.Equal(31337, received.WorkerProcessId);
        Assert.Equal("steady-state", received.RuntimeProfileName);
        Assert.Equal("tiered=off pgo=off r2r=off concurrentGc=off", received.RuntimeKnobs);
        Assert.True(received.RuntimeProfileApplied);
        Assert.Equal("net10.0", received.TargetFramework);
        Assert.Equal("1.2.3-preview.4", received.EngineVersion);
        Assert.Equal("Arm64", received.ProcessArchitecture);
    }

    /// <summary>
    ///     <see cref="ReadyPayload.RuntimeProfileApplied" /> must survive as <c>false</c>. A worker
    ///     should never report <c>false</c>, and the coordinator surfaces it when one does - so a
    ///     <c>false</c> that arrives as <c>true</c> would hide precisely the case the flag exists for:
    ///     the coordinator failed to set the environment block and the process boundary bought nothing.
    /// </summary>
    [Fact]
    public async Task Ready_WithNoProfileApplied_RoundTripsTheFalse()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ReadyPayload
            {
                ProtocolVersion = WorkerProtocol.Version,
                WorkerProcessId = 1,
                RuntimeProfileName = "inherited",
                RuntimeKnobs = "",
                RuntimeProfileApplied = false,
                TargetFramework = "net10.0",
                EngineVersion = "1.0.0",
                ProcessArchitecture = "X64",
            }),
            CancellationToken.None);

        var received = (await right.ReadAsync(CancellationToken.None))!.Ready!;

        Assert.False(received.RuntimeProfileApplied);
        Assert.Equal("", received.RuntimeKnobs);
    }

    /// <summary>
    ///     Every progress callback, because the coordinator replays these into the user's own
    ///     <see cref="IBenchmarkProgress" /> as though they had been raised locally. <c>Index</c> and
    ///     <c>Total</c> are not <c>required</c> and default to <c>0</c>, so a member that fails to
    ///     cross reads as a legitimate "indeterminate" rather than as an error.
    /// </summary>
    /// <remarks>
    ///     Parameterized by name rather than by the enum itself, because
    ///     <see cref="ProgressCallback" /> is internal and a public test signature cannot take it. The
    ///     cases still come from <c>Enum.GetNames</c>, so a new callback is covered without touching
    ///     this test.
    /// </remarks>
    public static TheoryData<string> ProgressCallbackNames
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var name in Enum.GetNames<ProgressCallback>())
                data.Add(name);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProgressCallbackNames))]
    public async Task Progress_RoundTripsEveryCallback(string callbackName)
    {
        var callback = Enum.Parse<ProgressCallback>(callbackName);

        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ProgressPayload
            {
                Callback = callback,
                Name = "Bench.Body",
                Index = 17,
                Total = 42,
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Equal(WorkerFrameKind.Progress, frame!.Kind);

        var received = frame.Progress!;

        Assert.Equal(callback, received.Callback);
        Assert.Equal("Bench.Body", received.Name);
        Assert.Equal(17, received.Index);
        Assert.Equal(42, received.Total);
    }

    /// <summary>
    ///     The widest payload on the wire, and the one whose shape is most able to lie.
    ///     <see cref="ObserverPhasePayload.Succeeded" /> defaults to <c>true</c>, so a member that
    ///     serializes but does not come back reports a crashed suite as a clean one - the exact failure
    ///     class <see cref="FrameChannel.SerializerOptions" /> cites these tests as the guard against.
    ///     Asserted field by field with <c>Succeeded = false</c> rather than through a populated-object
    ///     comparison, so a regression names the member it lost.
    /// </summary>
    [Fact]
    public async Task ObserverPhase_RoundTripsEveryField_IncludingSucceededFalse()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ObserverPhasePayload
            {
                Phase = MeasurementPhase.Warmup,
                Transition = PhaseTransition.Completed,
                BenchmarkName = "Bench.Body",
                JitterMetric = 0.42,
                DetectorSwitched = true,
                ResolvedK = 4096,
                ResolvedWarmup = 17,
                WarmupStop = WarmupStopReason.WallClockCap,
                SampleStop = SampleStopReason.DriftUnresolved,
                Succeeded = false,
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Equal(WorkerFrameKind.ObserverPhase, frame!.Kind);

        var received = frame.ObserverPhase!;

        Assert.Equal(MeasurementPhase.Warmup, received.Phase);
        Assert.Equal(PhaseTransition.Completed, received.Transition);
        Assert.Equal("Bench.Body", received.BenchmarkName);
        Assert.Equal(0.42, received.JitterMetric);
        Assert.True(received.DetectorSwitched);
        Assert.Equal(4096, received.ResolvedK);
        Assert.Equal(17, received.ResolvedWarmup);
        Assert.Equal(WarmupStopReason.WallClockCap, received.WarmupStop);
        Assert.Equal(SampleStopReason.DriftUnresolved, received.SampleStop);
        Assert.False(received.Succeeded);
    }

    /// <summary>
    ///     A <see cref="PhaseTransition.Starting" /> event has null outcome fields, and they must arrive
    ///     null rather than as zeros. The coordinator passes them positionally into a
    ///     <see cref="MeasurementPhaseEvent" />, where <c>ResolvedK = 0</c> would read as a resolved
    ///     ops-per-sample of zero instead of "not resolved yet".
    /// </summary>
    [Fact]
    public async Task ObserverPhase_WithNoOptionalFields_RoundTripsThemAsNull()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ObserverPhasePayload
            {
                Phase = MeasurementPhase.Jitter,
                Transition = PhaseTransition.Starting,
                BenchmarkName = "Bench.Body",
            }),
            CancellationToken.None);

        var received = (await right.ReadAsync(CancellationToken.None))!.ObserverPhase!;

        Assert.Null(received.JitterMetric);
        Assert.Null(received.ResolvedK);
        Assert.Null(received.ResolvedWarmup);
        Assert.Null(received.WarmupStop);
        Assert.Null(received.SampleStop);
        Assert.False(received.DetectorSwitched);
        Assert.True(received.Succeeded);
    }

    /// <summary>
    ///     A fault's <see cref="FaultPayload.BenchmarkName" /> decides its blast radius:
    ///     <c>WorkerGroupRunner.ToErroredResults</c> branches on it, so a name that fails to cross
    ///     converts one benchmark's failure into a group fault that errors <i>every</i> row.
    /// </summary>
    [Fact]
    public async Task Fault_RoundTripsMessageDetailAndBenchmarkName()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new FaultPayload
            {
                Message = "Body could not be addressed.",
                Detail = "at Some.Type.Method()\n  at Other.Frame()",
                BenchmarkName = "Bench.Body",
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Equal(WorkerFrameKind.Fault, frame!.Kind);

        var received = frame.Fault!;

        Assert.Equal("Body could not be addressed.", received.Message);
        Assert.Equal("at Some.Type.Method()\n  at Other.Frame()", received.Detail);
        Assert.Equal("Bench.Body", received.BenchmarkName);
    }

    /// <summary>The other side of the branch above: no name means the whole group failed.</summary>
    [Fact]
    public async Task Fault_WithoutABenchmarkName_StaysAGroupFault()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new FaultPayload { Message = "Target assembly would not load." }),
            CancellationToken.None);

        var received = (await right.ReadAsync(CancellationToken.None))!.Fault!;

        Assert.Equal("Target assembly would not load.", received.Message);
        Assert.Null(received.Detail);
        Assert.Null(received.BenchmarkName);
    }

    /// <summary>
    ///     The load-bearing serialization case. <see cref="MeasurementOptions" /> is the one
    ///     payload with real depth - nested option records, a percentile list, a runtime profile
    ///     with an environment map - and the whole worker design rests on it being pure value
    ///     data that survives a round trip. If this breaks, the coordinator silently measures
    ///     under different settings than the user configured.
    /// </summary>
    [Fact]
    public async Task RunGroup_RoundTripsFullMeasurementOptions()
    {
        var options = MeasurementOptions.Default with
        {
            Iterations = 123,
            WarmupIterations = 7,
            OpsPerSample = 64,
            ConfidenceLevel = 0.99,
            SignificanceLevel = 0.01,
            OutlierMode = OutlierMode.MedianAbsoluteDeviation,
            TailMetricsBasis = TailMetricsBasis.Trimmed,
            Profile = MeasurementProfile.Independent,
            MinimumPracticalEffect = 0.25,
            EnableHistogram = false,
            HistogramBucketCount = 33,
            ReportedPercentiles = [0.5, 0.9, 0.999],
            ForceGcBeforeEachIteration = true,
            MeasureAllocations = false,
            Diagnostics = new DiagnosticsOptions { GcHeapInfo = true, CpuTime = true },
            AutoTune = AutoTuneOptions.Default with
            {
                MinSamples = 11,
                MaxSamples = 2222,
                CiTarget = 0.001,
                MaxTuningTime = TimeSpan.FromSeconds(42),
                MinWarmupTime = TimeSpan.FromMilliseconds(750),
                RequireJitQuiescence = false,
                CapBehavior = AutoTuneCapBehavior.Error,
            },
            Environment = new EnvironmentOptions { CpuAffinity = [1, 3], DedicatedHostGuidance = true },
            RuntimeProfile = RuntimeProfile.ServerGc,
            MaxRawSamples = 512,
        };

        var payload = new RunGroupPayload
        {
            GroupId = "g1",
            Kind = WorkGroupKind.DiscoveredClass,
            TargetAssemblyPath = "/tmp/target.dll",
            DeclaringTypeFullName = "Some.Bench",
            BenchmarkNames = ["A", "B"],
            Options = options,
            Order = RunOrder.Random,
            Seed = 99,
            DisplayPrefix = "pfx",
            DefaultInstanceLifetime = InstanceLifetime.PerClass,
            StartIndex = 4,
            TotalBenchmarks = 10,
            MeasureCalibration = true,
        };

        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(WorkerFrame.Of(payload), CancellationToken.None);
        var frame = await right.ReadAsync(CancellationToken.None);

        var received = frame!.RunGroup!;

        Assert.Equal("g1", received.GroupId);
        Assert.Equal(WorkGroupKind.DiscoveredClass, received.Kind);
        Assert.Equal("Some.Bench", received.DeclaringTypeFullName);
        Assert.Equal(["A", "B"], received.BenchmarkNames);
        Assert.Equal(RunOrder.Random, received.Order);
        Assert.Equal(99, received.Seed);
        Assert.Equal("pfx", received.DisplayPrefix);
        Assert.Equal(InstanceLifetime.PerClass, received.DefaultInstanceLifetime);
        Assert.Equal(4, received.StartIndex);
        Assert.Equal(10, received.TotalBenchmarks);
        Assert.True(received.MeasureCalibration);

        var actual = received.Options;

        Assert.Equal(123, actual.Iterations);
        Assert.Equal(7, actual.WarmupIterations);
        Assert.Equal(64, actual.OpsPerSample);
        Assert.Equal(0.99, actual.ConfidenceLevel);
        Assert.Equal(0.01, actual.SignificanceLevel);
        Assert.Equal(OutlierMode.MedianAbsoluteDeviation, actual.OutlierMode);
        Assert.Equal(TailMetricsBasis.Trimmed, actual.TailMetricsBasis);
        Assert.Equal(MeasurementProfile.Independent, actual.Profile);
        Assert.Equal(0.25, actual.MinimumPracticalEffect);
        Assert.False(actual.EnableHistogram);
        Assert.Equal(33, actual.HistogramBucketCount);
        Assert.Equal(512, actual.MaxRawSamples);
        Assert.Equal([0.5, 0.9, 0.999], actual.ReportedPercentiles);
        Assert.True(actual.ForceGcBeforeEachIteration);
        Assert.False(actual.MeasureAllocations);
        Assert.True(actual.Diagnostics.GcHeapInfo);
        Assert.True(actual.Diagnostics.CpuTime);

        Assert.Equal(11, actual.AutoTune.MinSamples);
        Assert.Equal(2222, actual.AutoTune.MaxSamples);
        Assert.Equal(0.001, actual.AutoTune.CiTarget);
        Assert.Equal(TimeSpan.FromSeconds(42), actual.AutoTune.MaxTuningTime);
        Assert.Equal(TimeSpan.FromMilliseconds(750), actual.AutoTune.MinWarmupTime);
        Assert.False(actual.AutoTune.RequireJitQuiescence);
        Assert.Equal(AutoTuneCapBehavior.Error, actual.AutoTune.CapBehavior);

        Assert.Equal([1, 3], actual.Environment!.CpuAffinity);
        Assert.True(actual.Environment.DedicatedHostGuidance);

        // The knobs are what the process boundary exists to deliver, so they must survive it
        // exactly - a dropped knob would silently measure under the wrong configuration.
        Assert.Equal("server-gc", actual.RuntimeProfile.Name);
        Assert.Equal(RuntimeProfile.ServerGc.ToEnvironment(), actual.RuntimeProfile.ToEnvironment());
    }

    /// <summary>
    ///     A strategy object is live code and cannot travel as data. It must be dropped silently
    ///     on the wire (the type name travels instead) rather than throwing inside the serializer,
    ///     which is what an interface-typed property does without <c>[JsonIgnore]</c>.
    /// </summary>
    [Fact]
    public async Task RunGroup_DropsStrategyInstances_WithoutFailingToSerialize()
    {
        var options = MeasurementOptions.Default with
        {
            OutlierDetector = static () => OutlierDetectors.ForMode(OutlierMode.MedianAbsoluteDeviation),
            SignificanceTest = static () => DefaultSignificanceTest.Instance,
        };

        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new RunGroupPayload
            {
                GroupId = "g",
                Kind = WorkGroupKind.Lambdas,
                TargetAssemblyPath = "/tmp/t.dll",
                Options = options,
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Null(frame!.RunGroup!.Options.OutlierDetector);
        Assert.Null(frame.RunGroup.Options.SignificanceTest);
    }

    /// <summary>
    ///     Raw samples ride inside the completion frame next to their own result. This is the
    ///     structural fix for the defect that emptied <see cref="BenchmarkResult.RawSamples" /> on
    ///     every isolated result: with no side dictionary there is no key, so there is no key to
    ///     mismatch.
    /// </summary>
    [Fact]
    public async Task BenchmarkCompleted_CarriesResultAndSamplesTogether()
    {
        var samples = Enumerable.Range(0, 4096).Select(i => i * 1.5).ToArray();

        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new BenchmarkCompletedPayload
            {
                Result = new BenchmarkResult
                {
                    Name = "Bench.Body",
                    Mean = 12.5,
                    Median = 12.0,
                    Percentiles = [new PercentileEntry(0.5, 12.0)],
                    Min = 10,
                    Max = 30,
                    StandardDeviation = 1.25,
                    Q1 = 11,
                    Q3 = 13,
                    InterquartileRange = 2,
                    OutliersRemoved = 3,
                    N = 4096,
                    Skewness = 0.1,
                    Kurtosis = 0.2,
                    Mad = 0.5,
                    AllocMedian = 24,
                    AllocP95 = 24,
                    AllocMax = 24,
                    RuntimeProfileName = "steady-state",
                    RuntimeKnobs = "tiered=off pgo=off r2r=off concurrentGc=off",
                },
                RawSamples = samples,
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);
        var payload = frame!.BenchmarkCompleted!;

        Assert.Equal("Bench.Body", payload.Result.Name);
        Assert.Equal("steady-state", payload.Result.RuntimeProfileName);
        Assert.Equal(4096, payload.RawSamples.Length);
        Assert.Equal(samples, payload.RawSamples);
    }

    /// <summary>
    ///     D7: a benchmark parameter's value crosses as a formatted display string and its type name
    ///     rather than the raw <c>object?</c> <see cref="BenchmarkParameter.Value" /> declares - see
    ///     <see cref="BenchmarkParameterConverter" />. A <c>Type</c> value is the one that used to
    ///     make the whole frame write throw; an enum is the one that used to arrive as its
    ///     underlying number instead of its member name.
    /// </summary>
    [Fact]
    public async Task BenchmarkParameter_CrossesAsAFormattedValue_NotARawObject()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new BenchmarkCompletedPayload
            {
                Result = ResultNamed("Bench.Typed") with
                {
                    ParameterSet =
                    [
                        new BenchmarkParameter("kind", typeof(string)),
                        new BenchmarkParameter("mode", DayOfWeek.Friday),
                        new BenchmarkParameter("n", 42),
                        new BenchmarkParameter("nothing", null),
                    ],
                },
                RawSamples = [],
            }),
            CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);
        var parameters = frame!.BenchmarkCompleted!.Result.ParameterSet;

        Assert.Equal("System.String", BenchmarkParameter.FormatValue(parameters[0].Value));
        Assert.Equal("Friday", BenchmarkParameter.FormatValue(parameters[1].Value));
        Assert.Equal("42", BenchmarkParameter.FormatValue(parameters[2].Value));
        Assert.Null(parameters[3].Value);

        // The type component the grouping key carries for the enum matches what the same value's
        // key would carry in-process - not System.Text.Json.JsonElement for every parameter alike.
        var crossedKey = BenchmarkParameter.GetKey([parameters[1]]);
        var inProcessKey = BenchmarkParameter.GetKey([new BenchmarkParameter("mode", DayOfWeek.Friday)]);

        Assert.Equal(inProcessKey, crossedKey);
    }

    /// <summary>
    ///     The coalesced sample stream. Every field has to survive, because the coordinator rebuilds a
    ///     <see cref="SampleEvent" /> from it and hands that to the user's observer as though it had
    ///     been emitted locally - a dropped <c>Warmup</c> flag would silently move warmup samples into
    ///     a consumer's measured series.
    /// </summary>
    [Fact]
    public async Task ObserverSamples_RoundTripsAWholeBatch()
    {
        var batch = Enumerable.Range(0, 128)
            .Select(i => new ObserverSampleEntry($"Bench.Body", i, 2.5 + i, 64, i * 24L, i < 8))
            .ToArray();

        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ObserverSamplesPayload { Samples = batch }), CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Equal(WorkerFrameKind.ObserverSamples, frame!.Kind);
        Assert.Equal(batch, frame.ObserverSamples!.Samples);
    }

    /// <summary>
    ///     A detector snapshot's CI half-width is the live convergence signal, and it is legitimately
    ///     zero or non-finite early in a run - so this rides the same named-literal handling the
    ///     result frame needs.
    /// </summary>
    [Fact]
    public async Task ObserverDetector_RoundTrips()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new ObserverDetectorPayload
            {
                BenchmarkName = "Bench.Body",
                Phase = MeasurementPhase.Measurement,
                SampleCount = 512,
                Mean = 2.53,
                StdDev = 0.0,
                CiHalfWidth = double.NaN,
                CurrentK = 4096,
            }),
            CancellationToken.None);

        var received = (await right.ReadAsync(CancellationToken.None))!.ObserverDetector!;

        Assert.Equal("Bench.Body", received.BenchmarkName);
        Assert.Equal(MeasurementPhase.Measurement, received.Phase);
        Assert.Equal(512, received.SampleCount);
        Assert.Equal(2.53, received.Mean);
        Assert.Equal(0.0, received.StdDev);
        Assert.True(double.IsNaN(received.CiHalfWidth));
        Assert.Equal(4096, received.CurrentK);
    }

    /// <summary>
    ///     Many frames back to back, sized to straddle the pipe buffer, so a payload that arrives
    ///     in several reads is reassembled rather than misread as a truncated frame.
    /// </summary>
    [Fact]
    public async Task ManyLargeFrames_StayInSync()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        const int count = 40;

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
            {
                await left.WriteAsync(
                    WorkerFrame.Of(new BenchmarkCompletedPayload
                    {
                        Result = ResultNamed($"b{i}"),
                        RawSamples = Enumerable.Range(0, 5000).Select(x => (double)(x + i)).ToArray(),
                    }),
                    CancellationToken.None);
            }
        });

        for (var i = 0; i < count; i++)
        {
            var frame = await right.ReadAsync(CancellationToken.None);

            Assert.Equal($"b{i}", frame!.BenchmarkCompleted!.Result.Name);
            Assert.Equal(5000, frame.BenchmarkCompleted.RawSamples.Length);
            Assert.Equal(i, frame.BenchmarkCompleted.RawSamples[0]);
        }

        await writer;
    }

    /// <summary>
    ///     Non-finite statistics must survive the wire.
    ///     <para>
    ///         This is a regression guard for a defect that killed workers intermittently. A benchmark
    ///         whose samples are all identical has zero variance, so its skewness and kurtosis are
    ///         0/0 - and <c>Utf8JsonWriter</c> refuses to write <c>NaN</c> by default. The worker
    ///         therefore threw <i>after</i> measuring successfully, while serializing its own result,
    ///         and the coordinator saw nothing but a process that had gone away. Trivially fast bodies
    ///         hit it roughly a third of the time.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task BenchmarkCompleted_SurvivesNonFiniteStatistics()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        var result = ResultNamed("zero-variance") with
        {
            Skewness = double.NaN,
            Kurtosis = double.NaN,
            CoefficientOfVariation = double.NaN,
            OperationsPerSecond = double.PositiveInfinity,
            StandardDeviation = 0,
        };

        await left.WriteAsync(
            WorkerFrame.Of(new BenchmarkCompletedPayload { Result = result, RawSamples = [1.0, 1.0] }),
            CancellationToken.None);

        var received = (await right.ReadAsync(CancellationToken.None))!.BenchmarkCompleted!.Result;

        Assert.True(double.IsNaN(received.Skewness));
        Assert.True(double.IsNaN(received.Kurtosis));
        Assert.True(double.IsNaN(received.CoefficientOfVariation));
        Assert.True(double.IsPositiveInfinity(received.OperationsPerSecond));
    }

    /// <summary>
    ///     The terminal frame carries the worker's own calibration when one was asked for. It rides
    ///     here rather than beside a result because it describes the process, not any one benchmark.
    /// </summary>
    [Fact]
    public async Task GroupCompleted_CarriesTheWorkerCalibration()
    {
        var payload = new GroupCompletedPayload
        {
            GroupId = "g1",
            Calibration = new CalibrationPayload
            {
                Mean = 1234.5,
                Median = 1200.0,
                Samples = [1100.0, 1200.0, 1300.0],
            },
        };

        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(WorkerFrame.Of(payload), CancellationToken.None);
        var frame = await right.ReadAsync(CancellationToken.None);

        var received = frame!.GroupCompleted!;
        var calibration = received.Calibration!.ToResult();

        Assert.Equal("g1", received.GroupId);
        Assert.Equal(1234.5, calibration.Mean);
        Assert.Equal(1200.0, calibration.Median);
        Assert.Equal([1100.0, 1200.0, 1300.0], calibration.Samples);
    }

    [Fact]
    public async Task GroupCompleted_WithoutACalibration_RoundTripsAsNull()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(
            WorkerFrame.Of(new GroupCompletedPayload { GroupId = "g1" }), CancellationToken.None);

        var frame = await right.ReadAsync(CancellationToken.None);

        Assert.Null(frame!.GroupCompleted!.Calibration);
    }

    /// <summary>
    ///     End of stream reads as <c>null</c>, not an exception. This is what lets a worker exit on
    ///     its own when the coordinator dies, with no supervisor in the loop.
    /// </summary>
    [Fact]
    public async Task ClosedPeer_ReadsAsEndOfStream()
    {
        var (left, right, cleanup) = CreatePair();
        using var _ = cleanup;

        await left.WriteAsync(WorkerFrame.Shutdown(), CancellationToken.None);
        Assert.Equal(WorkerFrameKind.Shutdown, (await right.ReadAsync(CancellationToken.None))!.Kind);

        left.Dispose();

        Assert.Null(await right.ReadAsync(CancellationToken.None));
    }

    /// <summary>
    ///     A stream that is empty <i>before</i> any byte of a frame arrives is a clean end: the peer
    ///     closed the pipe between frames, and <c>null</c> is the signal the dispatch loops branch on
    ///     to exit quietly. Driven through a <see cref="MemoryStream" /> so the byte count is exact;
    ///     the real pipe is exercised by <see cref="ClosedPeer_ReadsAsEndOfStream" />.
    /// </summary>
    [Fact]
    public async Task EmptyStream_BeforeAnyByte_ReadsAsNull()
    {
        using var channel = new FrameChannel(new MemoryStream(), new MemoryStream());

        Assert.Null(await channel.ReadAsync(CancellationToken.None));
    }

    /// <summary>
    ///     A stream that dies <i>mid length-prefix</i> is not a clean end - two of the four framing
    ///     bytes arrived, so the peer was writing a frame and then vanished. <see cref="FrameChannel.ReadAsync" />
    ///     must surface that as a torn frame, not swallow it as <c>null</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the defect <c>FrameChannel.ReadExactlyAsync</c> carried: it returned <c>false</c>
    ///         whenever a read returned zero, <i>at any offset</i>, so a torn length prefix was
    ///         indistinguishable from a clean end. The length-prefix branch in <see cref="FrameChannel.ReadAsync" />
    ///         then turned that <c>false</c> into <c>null</c>, and a worker that had started writing a
    ///         frame and then crashed looked to the coordinator exactly like a worker that had
    ///         finished and exited - a silent <c>null</c> instead of a fault.
    ///     </para>
    ///     <para>
    ///         The fix: <c>FrameChannel.ReadExactlyAsync</c> returns <c>false</c> only when the stream ended
    ///         cleanly before <i>any</i> byte arrived; once it has begun filling the buffer, a
    ///         subsequent zero read is a torn frame and throws. The length-prefix branch's
    ///         <c>null</c> then means only "clean end between frames".
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TornLengthPrefix_ThrowsEndOfStream_NotReadsAsNull()
    {
        // Two of the four length-prefix bytes, then EOF.
        using var channel = new FrameChannel(
            new MemoryStream([0x78, 0x56]),
            new MemoryStream());

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => channel.ReadAsync(CancellationToken.None));
    }

    /// <summary>
    ///     A stream that dies <i>mid payload</i> - the length prefix arrived in full, but fewer than
    ///     the promised payload bytes did - is the other half of a torn frame. This already threw
    ///     before the <c>FrameChannel.ReadExactlyAsync</c> fix (the payload branch checks the return
    ///     value); the test pins it so a later "simplify" pass cannot quietly turn a torn payload
    ///     back into a <c>null</c>.
    /// </summary>
    [Fact]
    public async Task TornPayload_ThrowsEndOfStream()
    {
        // A length prefix promising 10 bytes, then only 3, then EOF.
        var torn = new MemoryStream([0x0A, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03]);
        using var channel = new FrameChannel(torn, new MemoryStream());

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => channel.ReadAsync(CancellationToken.None));
    }

    private static BenchmarkResult ResultNamed(string name) => new()
    {
        Name = name,
        Mean = 1,
        Median = 1,
        Percentiles = [],
        Min = 1,
        Max = 1,
        StandardDeviation = 0,
        Q1 = 1,
        Q3 = 1,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 1,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
    };
}
