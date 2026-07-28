using System.IO.Pipes;
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
    /// <summary>
    ///     Builds a connected pair of channels over two anonymous pipes, as the coordinator and
    ///     worker see them.
    /// </summary>
    private static (FrameChannel Left, FrameChannel Right, IDisposable Cleanup) CreatePair()
    {
        var leftToRight = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var rightToLeft = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

        var rightInbound = new AnonymousPipeClientStream(
            PipeDirection.In, leftToRight.GetClientHandleAsString());

        var rightOutbound = new AnonymousPipeClientStream(
            PipeDirection.Out, rightToLeft.GetClientHandleAsString());

        // Deliberately no DisposeLocalCopyOfClientHandle here. That call exists for the
        // cross-process case, where the child inherited a duplicate of the handle and the
        // parent's own copy must be closed so the child's exit is visible as end-of-stream.
        // Both ends live in this process, so the client stream wraps the very same handle and
        // closing it would break the pipe immediately.

        var left = new FrameChannel(rightToLeft, leftToRight);
        var right = new FrameChannel(rightInbound, rightOutbound);

        return (left, right, new Disposables(left, right));
    }

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                try
                {
                    item.Dispose();
                }
                catch (IOException)
                {
                    // The peer may already have torn the pipe down; nothing actionable.
                }
            }
        }
    }

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
            LaunchCount = 3,
            OutlierMode = OutlierMode.MedianAbsoluteDeviation,
            TailMetricsBasis = TailMetricsBasis.Trimmed,
            Profile = MeasurementProfile.Independent,
            MinimumPracticalEffect = 0.25,
            EnableHistogram = false,
            HistogramBucketCount = 33,
            ReportedPercentiles = [0.5, 0.9, 0.999],
            ForceGcBeforeEachIterationOverride = true,
            MeasureAllocationsOverride = false,
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
        };

        var payload = new RunGroupPayload
        {
            GroupId = "g1",
            Kind = WorkGroupKind.DiscoveredClass,
            TargetAssemblyPath = "/tmp/target.dll",
            DeclaringTypeFullName = "Some.Bench",
            BenchmarkNames = ["A", "B"],
            Options = options,
            OutlierDetectorTypeName = "Some.Detector, Some.Asm",
            Order = RunOrder.Random,
            Seed = 99,
            DisplayPrefix = "pfx",
            DefaultInstanceLifetime = InstanceLifetime.PerClass,
            StartIndex = 4,
            TotalBenchmarks = 10,
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
        Assert.Equal("Some.Detector, Some.Asm", received.OutlierDetectorTypeName);
        Assert.Equal(RunOrder.Random, received.Order);
        Assert.Equal(99, received.Seed);
        Assert.Equal("pfx", received.DisplayPrefix);
        Assert.Equal(InstanceLifetime.PerClass, received.DefaultInstanceLifetime);
        Assert.Equal(4, received.StartIndex);
        Assert.Equal(10, received.TotalBenchmarks);

        var actual = received.Options;

        Assert.Equal(123, actual.Iterations);
        Assert.Equal(7, actual.WarmupIterations);
        Assert.Equal(64, actual.OpsPerSample);
        Assert.Equal(0.99, actual.ConfidenceLevel);
        Assert.Equal(0.01, actual.SignificanceLevel);
        Assert.Equal(3, actual.LaunchCount);
        Assert.Equal(OutlierMode.MedianAbsoluteDeviation, actual.OutlierMode);
        Assert.Equal(TailMetricsBasis.Trimmed, actual.TailMetricsBasis);
        Assert.Equal(MeasurementProfile.Independent, actual.Profile);
        Assert.Equal(0.25, actual.MinimumPracticalEffect);
        Assert.False(actual.EnableHistogram);
        Assert.Equal(33, actual.HistogramBucketCount);
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
            OutlierDetector = OutlierDetectors.ForMode(OutlierMode.MedianAbsoluteDeviation),
            SignificanceTest = DefaultSignificanceTest.Instance,
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
                    RuntimeKnobs = "tiered=off pgo=off r2r=off",
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
    ///     End of stream reads as <c>null</c>, not an exception. This is what lets a worker exit
    ///     on its own when the coordinator dies, with no supervisor in the loop.
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
