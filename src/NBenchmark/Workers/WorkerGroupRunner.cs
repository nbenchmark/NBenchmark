using System.IO;
using System.Text.Json;
using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Drives one comparison group on a worker: send the request, replay the worker's telemetry into
///     the caller's own progress and observer instances, and collect the results.
/// </summary>
internal static class WorkerGroupRunner
{
    /// <summary>
    ///     What a worker produced for a group. Results and their samples arrive paired, so unlike
    ///     the previous protocol there is no separate keyed collection for them to fall out of.
    /// </summary>
    internal sealed record GroupResult
    {
        public required List<BenchmarkResult> Results { get; init; }
        public required Dictionary<string, double[]> RawSamples { get; init; }

        /// <summary>
        ///     Faults the worker reported. A fault with a benchmark name belongs to that benchmark;
        ///     one without belongs to the group.
        /// </summary>
        public required List<FaultPayload> Faults { get; init; }

        /// <summary>
        ///     <c>true</c> when the worker died before finishing the group, so nothing it did send
        ///     can be assumed complete.
        /// </summary>
        public bool WorkerDied { get; init; }

        /// <summary>
        ///     The worker's own measurement of <see cref="CalibrationStandard" />, when the request
        ///     asked for one. <c>null</c> means the caller must use its own.
        /// </summary>
        public CalibrationResult? Calibration { get; init; }
    }

    public static async Task<GroupResult> RunAsync(
        WorkerHost host,
        RunGroupPayload request,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);

        request = WithStreamingForObserver(request, observer);

        var results = new List<BenchmarkResult>();
        var samples = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var faults = new List<FaultPayload>();

        var idleTimeout = MeasurementBudget.IdleFrame(request.Options);

        // What the worker was last seen doing, so a timeout can name it. A worker that vanishes
        // silently is the hardest kind to diagnose, and "no frames since BenchmarkStarting for
        // Foo.Bar" is the difference between a usable report and "it hung".
        var lastActivity = "the run request was sent";

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Reset on every frame rather than allocated per read: a group can stream tens of thousands
        // of progress frames, and a CancellationTokenSource per frame would be pure garbage.
        using var idleCts = new CancellationTokenSource(idleTimeout);
        using var readLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, idleCts.Token);

        try
        {
            await host.Channel.WriteAsync(WorkerFrame.Of(request), linked.Token).ConfigureAwait(false);

            while (true)
            {
                WorkerFrame? frame;

                try
                {
                    frame = await host.Channel.ReadAsync(readLinked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested
                                                        && !linked.IsCancellationRequested)
                {
                    faults.Add(new FaultPayload
                    {
                        Message = $"The measurement worker sent nothing for {idleTimeout.TotalSeconds:0.#}s and "
                                  + $"was stopped. The last thing it reported was that {lastActivity}. A worker "
                                  + "goes quiet this long only when a benchmark body, a [GlobalSetup] or a "
                                  + "static initializer is blocked - on a lock, on I/O, or on an await that "
                                  + "never completes."
                                  + (host.StderrTail.Length == 0 ? "" : $" Worker stderr: {host.StderrTail}"),
                    });

                    return new GroupResult
                    {
                        Results = WithDeathWarning(results),
                        RawSamples = samples,
                        Faults = faults,
                        WorkerDied = true,
                    };
                }

                // Any frame at all is proof of life, including a coalesced progress tick.
                idleCts.CancelAfter(idleTimeout);

                if (frame is null)
                {
                    // Give the worker a moment to finish exiting and its stderr to drain. Composing
                    // the diagnostic immediately would race both, and produce exactly the useless
                    // "it vanished, no idea why" message this is meant to avoid.
                    await host.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                    faults.Add(new FaultPayload
                    {
                        Message = $"The measurement worker exited before the group finished ({host.ExitDescription})."
                                  + (host.StderrTail.Length == 0 ? "" : $" Worker stderr: {host.StderrTail}"),
                    });

                    return new GroupResult
                    {
                        Results = WithDeathWarning(results),
                        RawSamples = samples,
                        Faults = faults,
                        WorkerDied = true,
                    };
                }

                switch (frame.Kind)
                {
                    case WorkerFrameKind.Progress when frame.Progress is not null:
                        lastActivity = DescribeProgress(frame.Progress);
                        await ReplayProgressAsync(frame.Progress, progress, cancellationToken).ConfigureAwait(false);
                        break;

                    case WorkerFrameKind.ObserverPhase when frame.ObserverPhase is not null:
                        lastActivity = $"'{frame.ObserverPhase.BenchmarkName}' entered "
                                       + $"{frame.ObserverPhase.Phase}";

                        ReplayPhase(frame.ObserverPhase, observer);
                        break;

                    case WorkerFrameKind.ObserverSamples when frame.ObserverSamples is not null:
                        var batch = frame.ObserverSamples.Samples;

                        if (batch.Count > 0)
                        {
                            var last = batch[^1];
                            lastActivity = $"'{last.BenchmarkName}' was on sample {last.Ordinal}";
                        }

                        ReplaySamples(batch, observer);
                        break;

                    case WorkerFrameKind.ObserverDetector when frame.ObserverDetector is not null:
                        lastActivity = $"'{frame.ObserverDetector.BenchmarkName}' updated its "
                                       + $"{frame.ObserverDetector.Phase} detector";

                        ReplayDetector(frame.ObserverDetector, observer);
                        break;

                    case WorkerFrameKind.BenchmarkCompleted when frame.BenchmarkCompleted is not null:
                        var payload = frame.BenchmarkCompleted;
                        lastActivity = $"'{payload.Result.Name}' finished";

                        // Stamped here rather than in the worker, because only this side knows the
                        // result arrived over a process boundary at all. A worker that stamped
                        // itself would be taking its own word for it.
                        //
                        // This is the single choke point for the streaming path: BenchmarkResult
                        // defaults to InProcessRequested, so every result the worker sent arrives
                        // labelled host-measured and is promoted only here. That is the direction the
                        // default is chosen to fail in - a forgotten stamp under-claims - so do not
                        // move this into the worker to save the re-allocation.
                        results.Add(payload.Result with { IsolationStatus = IsolationStatus.Isolated });
                        samples[payload.Result.Name] = payload.RawSamples;

                        // Deliberately no OnBenchmarkCompletedAsync / OnResult here. A group may be
                        // measured by several replicate workers, and the result a consumer should
                        // see is the aggregate across them - not one per replicate. Only the caller
                        // knows when it holds the final result, so the caller raises it.
                        break;

                    case WorkerFrameKind.Fault when frame.Fault is not null:
                        faults.Add(frame.Fault);
                        break;

                    case WorkerFrameKind.GroupCompleted:
                        return new GroupResult
                        {
                            Results = results,
                            RawSamples = samples,
                            Faults = faults,
                            Calibration = frame.GroupCompleted?.Calibration?.ToResult(),
                        };

                    default:
                        faults.Add(new FaultPayload
                        {
                            Message = $"The measurement worker sent an unexpected {frame.Kind} frame.",
                        });

                        break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            faults.Add(new FaultPayload
            {
                Message = $"The measurement worker exceeded its {timeout.TotalSeconds:0.#}s ceiling and was "
                          + "stopped. This usually means a benchmark body deadlocked or waited on I/O that "
                          + "never completed. Raise the budget with --max-tuning-time if the work is "
                          + "genuinely this slow."
                          + (host.StderrTail.Length == 0 ? "" : $" Worker stderr: {host.StderrTail}"),
            });

            return new GroupResult
            {
                Results = WithDeathWarning(results),
                RawSamples = samples,
                Faults = faults,
                WorkerDied = true,
            };
        }
        catch (Exception ex) when (ex is IOException
                                       or JsonException
                                       or InvalidDataException
                                       or BenchmarkExecutionException)
        {
            // A torn or unreadable frame: the worker died while writing, or the stream desynchronized.
            // <see cref="FrameChannel.ReadAsync" /> throws <see cref="EndOfStreamException" /> (an
            // <see cref="IOException" />) when the pipe dies mid-frame, <see cref="InvalidDataException" />
            // on a bad length prefix, and <see cref="JsonException" /> on a corrupt payload.
            //
            // <see cref="BenchmarkExecutionException" /> is the one that comes from *this* side:
            // writing a frame past the protocol's size ceiling. Every other transport failure in this method is
            // turned into a fault, and that one was not - so it escaped here, escaped the launcher, and
            // took down the benchmark program over a frame that could simply have been reported. None of
            // these is the user's fault or something a retry of this group would fix, and none should take down
            // the whole benchmark program - which is what happened before this catch, because every
            // caller was a bare await and <c>ProcessWorkerLauncher</c> caught only
            // <c>WorkerStartException</c>. A worker that hard-crashes mid-payload-write is the reachable
            // case: a <c>BenchmarkCompleted</c> frame carrying thousands of samples is far larger than the
            // pipe buffer, so the prefix and payload cross as separate writes and the process can die
            // between them.
            //
            // Results already received survive in the local <c>results</c> list; the benchmarks the
            // worker never reported are filled in downstream by <see cref="ToErroredResults" />.
            // Settling the exit first means <see cref="WorkerHost.ExitDescription" /> names the cause
            // (an OOM kill, a stack overflow) and the stderr tail has drained, rather than racing both
            // into a useless "it vanished" message.
            await host.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            faults.Add(new FaultPayload
            {
                Message = $"The measurement worker died mid-frame and the stream became unreadable "
                          + $"({host.ExitDescription}): {ex.GetType().Name}. Results it had already sent "
                          + "were kept; any benchmark it never reported is shown as an error."
                          + (host.StderrTail.Length == 0 ? "" : $" Worker stderr: {host.StderrTail}"),
            });

            return new GroupResult
            {
                Results = WithDeathWarning(results),
                RawSamples = samples,
                Faults = faults,
                WorkerDied = true,
            };
        }
    }

    /// <summary>
    ///     A phrase naming what the worker last reported, for a timeout message. Reads as the tail
    ///     of "the last thing it reported was that ...".
    /// </summary>
    private static string DescribeProgress(ProgressPayload payload)
        => payload.Callback switch
        {
            ProgressCallback.WarmupStarting => $"'{payload.Name}' began warming up",
            ProgressCallback.WarmupCompleted => $"'{payload.Name}' finished warming up",
            ProgressCallback.BenchmarkStarting => $"'{payload.Name}' started",
            ProgressCallback.SampleCompleted =>
                $"'{payload.Name}' completed iteration {payload.Index}",
            _ => $"'{payload.Name}' reported {payload.Callback}",
        };

    private static Task ReplayProgressAsync(
        ProgressPayload payload, IBenchmarkProgress progress, CancellationToken cancellationToken)
        => payload.Callback switch
        {
            ProgressCallback.WarmupStarting =>
                progress.OnWarmupStartingAsync(payload.Name, payload.Total, cancellationToken),
            ProgressCallback.WarmupCompleted =>
                progress.OnWarmupCompletedAsync(payload.Name, cancellationToken),
            ProgressCallback.BenchmarkStarting =>
                progress.OnBenchmarkStartingAsync(payload.Name, payload.Index, payload.Total, cancellationToken),
            ProgressCallback.SampleCompleted =>
                progress.OnSampleCompletedAsync(payload.Name, payload.Index, payload.Total, cancellationToken),
            _ => Task.CompletedTask,
        };

    private static void ReplayPhase(ObserverPhasePayload payload, IMeasurementObserver observer)
        => observer.OnPhase(new MeasurementPhaseEvent(
            payload.BenchmarkName,
            payload.Phase,
            payload.Transition,
            payload.JitterMetric,
            payload.DetectorSwitched,
            payload.ResolvedK,
            payload.ResolvedWarmup,
            payload.WarmupStop,
            payload.SampleStop,
            payload.Succeeded));

    /// <summary>
    ///     Reconciles the request for a live sample stream with the observer that will receive it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two rules, both decided here rather than at each request-building call site because this
    ///         is the one place that holds both the request and the observer the stream would be
    ///         replayed into:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 An observer that declares <see cref="IMeasurementObserver.WantsSampleStream" />
    ///                 turns the stream <i>on</i> when the caller did not already ask for it. A
    ///                 live-streaming observer (e.g. a dashboard) attaches to see the per-sample stream,
    ///                 and requiring the caller to remember a separate flag would let the stream go
    ///                 silently absent - the exact failure mode this exists to prevent.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 The stream is <i>withdrawn</i> when no observer is attached to replay it into.
    ///                 Forwarding samples costs the worker frame encoding <i>during</i> the measurement
    ///                 (its volume scales with how fast the measured code is), so requesting it with
    ///                 nothing to replay it into is pure loss, and the wrong answer here is invisible -
    ///                 the run still produces every number it should, only slower. This also holds for
    ///                 the replicates that deliberately pass a null observer, where later workers would
    ///                 otherwise pay for events the coordinator drops on the floor.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    internal static RunGroupPayload WithStreamingForObserver(
        RunGroupPayload request,
        IMeasurementObserver observer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        // No observer to replay into: withdraw a stream the caller asked for, and never turn one on.
        if (observer == NullMeasurementObserver.Instance)
            return request.Options.StreamSamples
                ? request with { Options = request.Options with { StreamSamples = false } }
                : request;

        // An attached observer that wants the stream enables it when the caller did not ask.
        if (observer.WantsSampleStream && !request.Options.StreamSamples)
            return request with { Options = request.Options with { StreamSamples = true } };

        return request;
    }

    /// <summary>
    ///     Replays a coalesced sample batch, in the order the worker emitted it.
    /// </summary>
    /// <remarks>
    ///     The batching is a transport detail and is deliberately not visible to the observer: it
    ///     receives one <see cref="IMeasurementObserver.OnSample" /> call per sample, exactly as an
    ///     in-process run delivers them. What an isolated observer does see differently is timing -
    ///     the events arrive in bursts up to a batch behind the body that produced them - which is
    ///     the cost of not paying a frame per sample.
    /// </remarks>
    private static void ReplaySamples(IReadOnlyList<ObserverSampleEntry> batch, IMeasurementObserver observer)
    {
        foreach (var entry in batch)
        {
            observer.OnSample(new SampleEvent(
                entry.BenchmarkName, entry.Ordinal, entry.PerOpNs, entry.K, entry.AllocDelta, entry.Warmup));
        }
    }

    private static void ReplayDetector(ObserverDetectorPayload payload, IMeasurementObserver observer)
        => observer.OnDetector(new DetectorStateEvent(
            payload.BenchmarkName,
            payload.Phase,
            payload.SampleCount,
            payload.MeanNs,
            payload.StdDev,
            payload.CiHalfWidth,
            payload.CurrentK));

    /// <summary>
    ///     The warning stamped on every result a worker had already sent when the worker then died
    ///     before the group finished, so a consumer's report can tell a measured row from a row
    ///     measured by a process that then vanished.
    /// </summary>
    /// <remarks>
    ///     Coordinator-side because only this side observes the death. The worker's own contract for
    ///     <see cref="GroupResult.WorkerDied" /> is that nothing it sent can be assumed complete, and a
    ///     row that carries a result but no warning would read as fully trustworthy - directly
    ///     contradicting that contract. <see cref="ToErroredResults" /> only fills in benchmarks the
    ///     worker never reported, so it does not cover the rows that <i>did</i> arrive.
    /// </remarks>
    internal const string DeathWarning =
        "The measurement worker died before this group finished, so this result was already on the "
        + "wire but cannot be assumed complete.";

    /// <summary>
    ///     Returns a copy of <paramref name="results" /> with <see cref="DeathWarning" /> appended to
    ///     each row's warnings, preserving any warnings the row already carried. Called at every
    ///     <see cref="GroupResult.WorkerDied" /> return path.
    /// </summary>
    internal static List<BenchmarkResult> WithDeathWarning(List<BenchmarkResult> results)
    {
        if (results.Count == 0)
            return results;

        var stamped = new List<BenchmarkResult>(results.Count);

        foreach (var result in results)
            stamped.Add(result with { Warnings = [..result.Warnings, DeathWarning] });

        return stamped;
    }

    /// <summary>
    ///     Turns the group's faults into errored results, so a benchmark that could not be measured
    ///     appears in the table with the reason rather than silently going missing.
    /// </summary>
    public static IReadOnlyList<BenchmarkResult> ToErroredResults(
        GroupResult group,
        IReadOnlyList<string> expectedNames,
        string displayPrefix)
    {
        if (group.Faults.Count == 0)
            return [];

        var reported = new HashSet<string>(group.Results.Select(r => r.Name), StringComparer.Ordinal);
        var errored = new List<BenchmarkResult>();

        // A named fault is attributable to one benchmark.
        foreach (var fault in group.Faults.Where(f => f.BenchmarkName is { Length: > 0 }))
        {
            var name = Qualify(displayPrefix, fault.BenchmarkName!);

            if (reported.Add(name))
                errored.Add(ErroredResult(name, displayPrefix, fault.Message));
        }

        // An unnamed fault is the group's, so every benchmark the worker never reported gets it.
        var groupFault = group.Faults.FirstOrDefault(f => f.BenchmarkName is null or "");

        if (groupFault is not null)
        {
            foreach (var expected in expectedNames)
            {
                var name = Qualify(displayPrefix, expected);

                if (reported.Add(name))
                    errored.Add(ErroredResult(name, displayPrefix, groupFault.Message));
            }
        }

        return errored;
    }

    private static string Qualify(string prefix, string name)
        => string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

    /// <summary>
    ///     A placeholder row for a benchmark that could not be measured, carrying the reason. Shared
    ///     so every path reports a failure the same way rather than silently dropping the line.
    /// </summary>
    internal static BenchmarkResult ErroredResult(string name, string message)
        => ErroredResult(name, ClassNameFrom(name), message);

    internal static BenchmarkResult ErroredResult(string name, string className, string message) => new()
    {
        Name = name,
        ClassName = className,
        MeanNs = 0,
        MedianNs = 0,
        Percentiles = [],
        MinNs = 0,
        MaxNs = 0,
        StandardDeviationNs = 0,
        Q1Ns = 0,
        Q3Ns = 0,
        InterquartileRangeNs = 0,
        OutliersRemoved = 0,
        SampleCount = 0,
        Skewness = 0,
        Kurtosis = 0,
        MedianAbsoluteDeviationNs = 0,
        AllocatedBytesMedian = null,
        AllocatedBytesP95 = null,
        AllocatedBytesMax = null,
        Errored = true,
        ErrorMessage = message,
    };

    private static string ClassNameFrom(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator <= 0 ? "" : name[..separator];
    }
}
