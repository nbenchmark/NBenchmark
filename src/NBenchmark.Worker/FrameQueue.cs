using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     A serialized outbound queue in front of the pipe.
///     <para>
///         Every frame the worker emits during a group goes through here, for two reasons. First,
///         <see cref="IMeasurementObserver" /> forbids blocking - the measurement loop calls it
///         between samples on the timing-critical thread - so an observer callback must hand off and
///         return rather than wait on a pipe write. Second, ordering is preserved, so a progress
///         event for a benchmark can never overtake that benchmark's own completion frame.
///     </para>
///     <para>
///         Implemented as a task chain rather than a channel with a pump, because that makes
///         "everything enqueued so far has reached the pipe" a single await on the tail rather than
///         a poll.
///     </para>
/// </summary>
internal sealed class FrameQueue(FrameChannel channel, CancellationToken cancellationToken)
{
    private readonly FrameChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    // `object` rather than `System.Threading.Lock`, which does not exist on net8.0.
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>
    ///     Enqueues a frame. Returns immediately: the only work done on the caller's thread is
    ///     appending to the chain.
    /// </summary>
    public void Enqueue(WorkerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            var previous = _tail;

            _tail = Continue(previous, frame);
        }
    }

    private async Task Continue(Task previous, WorkerFrame frame)
    {
        await previous.ConfigureAwait(false);

        try
        {
            await _channel.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The coordinator is gone or shutting down. Swallowing keeps the chain usable so a
            // later frame is not blocked by an earlier failure, and there is nobody left to tell.
        }
    }

    /// <summary>
    ///     Waits for everything enqueued so far to reach the pipe. Called before a group's terminal
    ///     frame, so the coordinator never sees a group complete while its events are still in
    ///     flight.
    /// </summary>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }
}
