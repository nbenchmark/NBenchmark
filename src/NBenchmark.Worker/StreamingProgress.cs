using System.Diagnostics;
using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     Forwards the measuring process's live telemetry back to the coordinator, so an isolated
///     benchmark is as visible as an in-process one.
///     <para>
///         The previous isolated path had no channel for this at all: children ran silently, and a
///         progress bar simply stopped while the real work happened. That is what made isolation
///         feel like a black box.
///     </para>
///     <para>
///         Lifecycle callbacks, phase transitions and detector snapshots are forwarded always -
///         each is emitted a handful of times per benchmark. The per-sample stream is the one
///         exception: it is opt-in (<see cref="MeasurementOptions.StreamSamples" />) and coalesced,
///         because it is the only channel whose volume is a function of how fast the measured code
///         is. See <see cref="OnSample" />.
///     </para>
/// </summary>
internal sealed class StreamingProgress(
    FrameQueue queue,
    CancellationToken cancellationToken,
    bool streamSamples = false)
    : IBenchmarkProgress, IMeasurementObserver
{
    /// <summary>
    ///     Minimum gap between forwarded per-sample progress ticks. The engine calls
    ///     <see cref="OnIterationCompleted" /> once per sample and can take thousands of samples per
    ///     benchmark; forwarding each one would put tens of milliseconds of frame encoding into a
    ///     run whose whole point is measuring milliseconds. A progress bar cannot show more than
    ///     this anyway.
    /// </summary>
    private static readonly long CoalesceTicks = Stopwatch.Frequency / 20;

    /// <summary>
    ///     How many sample events accumulate before a batch is shipped. Bounds the frame size;
    ///     <see cref="SampleFlushTicks" /> bounds the latency.
    /// </summary>
    private const int SampleBatchSize = 128;

    /// <summary>
    ///     Longest a buffered sample may wait before its batch is shipped anyway - 100 ms, so a
    ///     consumer sees the stream at 10 Hz even from a body too slow to fill a batch.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately not the 50 ms <see cref="CoalesceTicks" /> uses for progress ticks, which
    ///         is a different question - how often a progress bar redraws, not how much of a sample
    ///         stream is held. 100 ms is where the two bounds meet: a fast body emits sample events at
    ///         roughly 1 kHz (auto-<c>K</c> targets a 10 µs sample and the engine emits every
    ///         fiftieth), so 100 ms is about the interval in which
    ///         <see cref="SampleBatchSize" /> samples accumulate. Measured on the real path, halving
    ///         the interval to 50 ms takes a benchmark from 8 frames to 13 for the same ~780 samples,
    ///         while doubling it to 200 ms leaves the count at 8 - past this point the batch size is
    ///         what binds and a wider interval buys nothing but latency.
    ///     </para>
    ///     <para>
    ///         Neither bound is measurable in the result. Interleaved against a control across eight
    ///         replicates, a streamed run's wall clock and reported median both sat inside the
    ///         control's own spread - the callback fires between samples, outside the timed region, and
    ///         the frame encoding happens on the queue's continuation rather than on the measurement
    ///         thread. The bounds exist to keep that true as the sample count scales, not to repair a
    ///         measured cost.
    ///     </para>
    /// </remarks>
    private static readonly long SampleFlushTicks = Stopwatch.Frequency / 10;

    private long _lastIterationTick;

    /// <summary>
    ///     The sample batch in flight. Only allocated when streaming was asked for, so an ordinary
    ///     run carries neither the list nor the branch's cost past one bool test.
    /// </summary>
    private readonly List<ObserverSampleEntry>? _samples =
        streamSamples ? new List<ObserverSampleEntry>(SampleBatchSize) : null;

    // `object` rather than `System.Threading.Lock`, which does not exist on net8.0. The observer
    // contract says one measurement thread, but the group's terminal flush comes from the session's
    // own continuation, so the buffer genuinely has two callers.
    private readonly object _sampleGate = new();

    private long _lastSampleFlushTick;

    /// <summary>
    ///     This type is both the progress sink and the observer sink. They are separate interfaces
    ///     with separate contracts, but one object can satisfy both, and sharing it keeps a single
    ///     ordered queue behind them.
    /// </summary>
    public IMeasurementObserver AsObserver() => this;

    // Suite-level callbacks are the coordinator's own business - it knows the full benchmark list
    // and a worker only ever sees one group of it, so a worker's view would be wrong.
    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) => Task.CompletedTask;

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Task.CompletedTask;

    public Task OnWarmupStarting(string name, int totalWarmupIterations)
    {
        Send(ProgressCallback.WarmupStarting, name, 0, totalWarmupIterations);
        return Task.CompletedTask;
    }

    public Task OnWarmupCompleted(string name)
    {
        Send(ProgressCallback.WarmupCompleted, name, 0, 0);
        return Task.CompletedTask;
    }

    public Task OnBenchmarkStarting(string name, int index, int total)
    {
        Send(ProgressCallback.BenchmarkStarting, name, index, total);
        return Task.CompletedTask;
    }

    public Task OnIterationCompleted(string name, int iteration, int totalIterations)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastIterationTick);

        if (now - last < CoalesceTicks)
            return Task.CompletedTask;

        Volatile.Write(ref _lastIterationTick, now);
        Send(ProgressCallback.IterationCompleted, name, iteration, totalIterations);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     The coordinator raises this itself from the benchmark's completion frame, which carries
    ///     the authoritative result. Forwarding it here as well would fire the callback twice.
    /// </summary>
    public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

    public void OnPhase(in MeasurementPhaseEvent e)
    {
        // A worker measures one group; the suite-completed sentinel belongs to the whole run and is
        // raised by the coordinator once, not once per worker.
        if (e.Phase == MeasurementPhase.SuiteCompleted)
            return;

        // Buffered samples belong to the phase that is ending, so they go out ahead of its boundary.
        // The queue preserves order, so flushing here is what keeps a replayed stream in the order
        // the engine emitted it rather than delivering a phase's samples after its own completion.
        FlushSamples();

        queue.Enqueue(WorkerFrame.Of(new ObserverPhasePayload
        {
            Phase = e.Phase,
            Transition = e.Transition,
            BenchmarkName = e.BenchmarkName,
            JitterMetric = e.JitterMetric,
            DetectorSwitched = e.DetectorSwitched,
            ResolvedK = e.ResolvedK,
            ResolvedWarmup = e.ResolvedWarmup,
            WarmupStop = e.WarmupStop,
            SampleStop = e.SampleStop,
            Succeeded = e.Succeeded,
        }));
    }

    /// <summary>
    ///     Forwarded only when the run asked for it (<see cref="MeasurementOptions.StreamSamples" />),
    ///     and then in coalesced batches rather than one frame per sample.
    ///     <para>
    ///         This is the only callback whose volume is a function of how fast the benchmarked code
    ///         is: a nanosecond body emits thousands of these where every other frame in the protocol
    ///         is emitted a handful of times. A frame each would put the cost of observing the run
    ///         inside the run, which is why it did not cross at all before. Batching bounds that cost
    ///         to a frame per <see cref="SampleBatchSize" /> samples or per
    ///         <see cref="SampleFlushTicks" />, whichever comes first - the count bound keeps a frame
    ///         small on a fast body, and the time bound keeps a slow body's stream live rather than
    ///         held until its phase ends.
    ///     </para>
    ///     <para>
    ///         Even opted in, this is more intrusive than the default. A consumer that only needs the
    ///         samples <i>eventually</i> should leave it off and read them off the completion frame.
    ///     </para>
    /// </summary>
    public void OnSample(in SampleEvent e)
    {
        if (_samples is null || cancellationToken.IsCancellationRequested)
            return;

        lock (_sampleGate)
        {
            _samples.Add(new ObserverSampleEntry(
                e.BenchmarkName, e.Ordinal, e.PerOpNs, e.K, e.AllocDelta, e.Warmup));

            var now = Stopwatch.GetTimestamp();

            if (_samples.Count >= SampleBatchSize || now - _lastSampleFlushTick >= SampleFlushTicks)
                FlushLocked(now);
        }
    }

    /// <summary>
    ///     Forwarded unconditionally, unlike <see cref="OnSample" />. A benchmark emits a handful of
    ///     these - one per calibration step, one if warmup recalibrates K, one when the measurement
    ///     stop rule resolves - so the volume is the same order as the phase frames that already
    ///     cross, and the live convergence curve is the single most useful "why did it stop" signal
    ///     for an observer to have. There is nothing here worth making opt-in.
    /// </summary>
    public void OnDetector(in DetectorStateEvent e)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        // Ahead of the snapshot, so the samples it summarizes are already delivered.
        FlushSamples();

        queue.Enqueue(WorkerFrame.Of(new ObserverDetectorPayload
        {
            BenchmarkName = e.BenchmarkName,
            Phase = e.Phase,
            SampleCount = e.SampleCount,
            Mean = e.Mean,
            StdDev = e.StdDev,
            CiHalfWidth = e.CiHalfWidth,
            CurrentK = e.CurrentK,
        }));
    }

    /// <summary>
    ///     Ships whatever samples are buffered. Called at every phase and detector boundary, and once
    ///     more by the session before the group's terminal frame, so no sample is left in the buffer
    ///     when the worker stops measuring.
    /// </summary>
    public void FlushSamples()
    {
        if (_samples is null)
            return;

        lock (_sampleGate)
        {
            if (_samples.Count > 0)
                FlushLocked(Stopwatch.GetTimestamp());
        }
    }

    private void FlushLocked(long now)
    {
        var batch = _samples!.ToArray();

        _samples.Clear();
        _lastSampleFlushTick = now;

        queue.Enqueue(WorkerFrame.Of(new ObserverSamplesPayload { Samples = batch }));
    }

    /// <summary>
    ///     Not forwarded: the completion frame carries the same result, and the coordinator raises
    ///     <see cref="IMeasurementObserver.OnResult" /> from it.
    /// </summary>
    public void OnResult(BenchmarkResult result)
    {
    }

    private void Send(ProgressCallback callback, string name, int index, int total)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        queue.Enqueue(WorkerFrame.Of(new ProgressPayload
        {
            Callback = callback,
            Name = name,
            Index = index,
            Total = total,
        }));
    }
}
