using System.Text.Json;
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
/// <param name="onTransportFailure">
///     Invoked at most once, the first time a write proves the coordinator can no longer be reached.
///     Failures are still swallowed - the chain has to stay usable - but "the coordinator cannot be
///     written to" and "there is no point continuing to measure" are the same fact, and something has
///     to carry it out of here. Only a genuine <i>transport</i> failure counts: a frame this process
///     cannot write is dropped with a line on stderr and does not claim the coordinator is gone.
/// </param>
internal sealed class FrameQueue(
    FrameChannel channel,
    CancellationToken cancellationToken,
    Action? onTransportFailure = null)
{
    private readonly FrameChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    // `object` rather than `System.Threading.Lock`, which does not exist on net8.0.
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private int _reportedFailure;

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
        // Awaited inside a try, because the chain has to survive a link that failed in a way this
        // method did not anticipate. It did not: `await previous` sat outside, so one unhandled write
        // failure faulted the tail, every later Enqueue rethrew it as its own, and the fault finally
        // escaped `DrainAsync` - which is awaited in a `finally`, so the group ended with no terminal
        // frame at all and the coordinator saw a worker that had simply vanished. One bad frame cost
        // the group and its diagnosis.
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // Whatever it was, the link that produced it has already said so.
        }

        try
        {
            await _channel.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe is gone. Still swallowed, so a later frame is not blocked by an earlier failure
            // and the chain stays usable - but reported once, because a worker that cannot reach its
            // coordinator should stop measuring rather than finish the group for nobody.
            if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
                onTransportFailure?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Already shutting down. Deliberately *not* reported as coordinator loss: that is the
            // benign case, and treating a cancellation as proof the coordinator died would make the
            // conclusion self-fulfilling once anything cancels the group.
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or NotSupportedException)
        {
            // This frame cannot be written, but the pipe is fine: it exceeded the protocol's size
            // ceiling, or it carries a value the serializer refuses - a `[Arguments(typeof(X))]`
            // argument is the reachable one. Deliberately *not* reported as transport failure, because
            // the coordinator is still there and every other frame will reach it. Dropping this one
            // loses a result; treating it as coordinator loss would lose the group.
            Console.Error.WriteLine(
                $"nbworker: a {frame.Kind} frame could not be sent and was dropped ({ex.GetType().Name}: "
                + $"{ex.Message}).");
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
