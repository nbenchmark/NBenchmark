using System.Threading.Channels;

namespace NBenchmark.Engine;

/// <summary>
///     An <see cref="IMeasurementObserver" /> that writes every event to a bounded
///     <see cref="Channel{MeasurementEvent}" /> with <see cref="BoundedChannelFullMode.DropOldest" />.
///     The writer side is non-blocking and allocation-free per event (the event is a value type);
///     the reader side can drain asynchronously on a background pump thread without back-pressuring
///     the measurement thread.
/// </summary>
/// <remarks>
///     This is the timing-safety shim described in the live-telemetry design: the measurement
///     loop sees an observer that does nothing but <see cref="ChannelWriter{T}.TryWrite" />.
///     A slow consumer can never perturb measurement because the bounded channel drops the
///     oldest event when full (<see cref="BoundedChannelFullMode.DropOldest" />) instead of
///     blocking the writer. The drop is silent and intentional: the live view degrades to
///     "latest wins" when the consumer cannot keep up.
/// </remarks>
public sealed class ChannelMeasurementObserver : IMeasurementObserver
{
    private readonly Channel<MeasurementEvent> _channel;
    private readonly ChannelWriter<MeasurementEvent> _writer;

    /// <summary>
    ///     Creates a channel-backed observer with the given <paramref name="capacity" />.
    ///     The channel uses <see cref="BoundedChannelFullMode.DropOldest" />.
    ///     <see cref="ChannelOptions.SingleWriter" /> is <c>false</c>: the sync runner path
    ///     writes from one thread, but the async path awaits the body with
    ///     <c>ConfigureAwait(false)</c> and its continuations can resume on any thread-pool
    ///     thread, so two overlapping writes are possible. The channel tolerates concurrent
    ///     writers; the bounded <see cref="BoundedChannelFullMode.DropOldest" /> mode keeps
    ///     writes non-blocking so the measurement thread is never back-pressured.
    /// </summary>
    public ChannelMeasurementObserver(int capacity = 1024)
    {
        _channel = Channel.CreateBounded<MeasurementEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
        });

        _writer = _channel.Writer;
    }

    /// <summary>
    ///     The reader side of the channel. Drain this on a background thread.
    ///     When the writer completes (the benchmark finishes), complete the writer so
    ///     the reader's <see cref="ChannelReader{T}.Completion" /> signals the pump to stop.
    /// </summary>
    public ChannelReader<MeasurementEvent> Reader => _channel.Reader;

    /// <summary>
    ///     This observer's primary value is live telemetry, so attachers should receive the
    ///     per-sample stream across worker boundaries without having to set an extra option.
    /// </summary>
    public bool WantsSampleStream => true;

    public void OnPhase(in MeasurementPhaseEvent e) => _writer.TryWrite(new MeasurementEvent(e));

    public void OnSample(in SampleEvent e) => _writer.TryWrite(new MeasurementEvent(e));

    public void OnDetector(in DetectorStateEvent e) => _writer.TryWrite(new MeasurementEvent(e));

    public void OnResult(BenchmarkResult result)
    {
        // The interface contract allows a null result (NullMeasurementObserver.OnResult is
        // exercised with null! in tests, and an errored outcome can carry a null Result on
        // pre-runner failure sites). Drop the event rather than enqueuing an ambiguous
        // Kind=Result / Result=null frame that a consumer would have to special-case.
        if (result is null)
            return;

        _writer.TryWrite(new MeasurementEvent(result));
    }

    /// <summary>
    ///     Completes the channel writer so a reader awaiting
    ///     <see cref="ChannelReader{T}.Completion" /> stops blocking after the buffered events
    ///     drain. The harness and suite wrap the resolved observer in a <c>using</c>, so this
    ///     runs automatically at the end of <c>RunAsync</c> without the caller needing to call
    ///     <see cref="Complete" /> explicitly. The channel itself is GC'd; there are no
    ///     unmanaged resources to release. Safe to call multiple times (subsequent
    ///     <see cref="ChannelWriter{T}.TryComplete" /> calls are no-ops).
    /// </summary>
    public void Dispose() => _writer.TryComplete();

    /// <summary>
    ///     Signals that no more events will be written. Call this when the benchmark run
    ///     completes so the reader pump can stop (the reader will see
    ///     <see cref="ChannelReader{T}.Completion" /> complete after all buffered events
    ///     are drained).
    /// </summary>
    public void Complete() => _writer.TryComplete();
}
