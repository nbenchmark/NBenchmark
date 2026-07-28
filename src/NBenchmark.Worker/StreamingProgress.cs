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
/// </summary>
internal sealed class StreamingProgress(FrameQueue queue, CancellationToken cancellationToken)
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

    private long _lastIterationTick;

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
    ///     Per-sample observer events are not forwarded. A benchmark can emit thousands, and the
    ///     encoding cost would land inside the measured run - the opposite of what a worker is for.
    ///     An observer attached in the coordinator still sees every phase transition and every
    ///     result; only the raw per-sample stream stops at the process boundary, and the samples
    ///     themselves arrive in full on the completion frame.
    /// </summary>
    public void OnSample(in SampleEvent e)
    {
    }

    /// <summary>Not forwarded, for the reason given on <see cref="OnSample" />.</summary>
    public void OnDetector(in DetectorStateEvent e)
    {
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
