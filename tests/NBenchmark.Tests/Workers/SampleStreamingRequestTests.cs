using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The rule deciding whether a worker is asked to forward its live per-sample stream.
///     <para>
///         Worth its own tests because it is the only place in the protocol where the coordinator
///         withdraws something the caller asked for: the stream costs frame encoding <i>inside</i> the
///         measurement, so requesting it with nothing to replay it into is pure loss, and the wrong
///         answer here is invisible - the run still produces every number it should, only slower.
///     </para>
/// </summary>
public sealed class SampleStreamingRequestTests
{
    private static RunGroupPayload Request(bool streamSamples) => new()
    {
        GroupId = "g",
        Kind = WorkGroupKind.DiscoveredClass,
        TargetAssemblyPath = "/tmp/target.dll",
        Options = MeasurementOptions.Default with { StreamSamples = streamSamples },
    };

    [Fact]
    public void AnAttachedObserver_KeepsTheStream()
    {
        var request = WorkerGroupRunner.WithStreamingForObserver(Request(true), new CountingObserver());

        Assert.True(request.Options.StreamSamples);
    }

    [Fact]
    public void NoObserver_WithdrawsTheStream()
    {
        var request = WorkerGroupRunner.WithStreamingForObserver(
            Request(true), NullMeasurementObserver.Instance);

        Assert.False(request.Options.StreamSamples);
    }

    /// <summary>
    ///     An attached observer must not turn the stream on by itself. It is opt-in because the volume
    ///     scales with how fast the measured code is, so attaching a live observer cannot be allowed
    ///     to silently make the run more intrusive than the user asked for.
    /// </summary>
    [Fact]
    public void AnAttachedObserver_DoesNotTurnTheStreamOn()
    {
        var request = WorkerGroupRunner.WithStreamingForObserver(Request(false), new CountingObserver());

        Assert.False(request.Options.StreamSamples);
    }

    /// <summary>
    ///     The common case, asserted so the check never becomes a per-group allocation: an untouched
    ///     request comes back as the very same instance.
    /// </summary>
    [Fact]
    public void ARequestThatNeedsNoChange_IsNotRewritten()
    {
        var original = Request(false);

        Assert.Same(original, WorkerGroupRunner.WithStreamingForObserver(original, new CountingObserver()));
        Assert.Same(
            original,
            WorkerGroupRunner.WithStreamingForObserver(original, NullMeasurementObserver.Instance));
    }

    private sealed class CountingObserver : IMeasurementObserver
    {
        public void OnPhase(in MeasurementPhaseEvent e)
        {
        }

        public void OnSample(in SampleEvent e)
        {
        }

        public void OnDetector(in DetectorStateEvent e)
        {
        }

        public void OnResult(BenchmarkResult result)
        {
        }
    }
}
