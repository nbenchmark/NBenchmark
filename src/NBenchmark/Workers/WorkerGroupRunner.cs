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
                        Results = results,
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
                        Results = results,
                        RawSamples = samples,
                        Faults = faults,
                        WorkerDied = true,
                    };
                }

                switch (frame.Kind)
                {
                    case WorkerFrameKind.Progress when frame.Progress is not null:
                        lastActivity = DescribeProgress(frame.Progress);
                        await ReplayProgressAsync(frame.Progress, progress).ConfigureAwait(false);
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

                        // Deliberately no OnBenchmarkCompleted / OnResult here. A group may be
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
                Results = results,
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
            ProgressCallback.IterationCompleted =>
                $"'{payload.Name}' completed iteration {payload.Index}",
            _ => $"'{payload.Name}' reported {payload.Callback}",
        };

    private static Task ReplayProgressAsync(ProgressPayload payload, IBenchmarkProgress progress)
        => payload.Callback switch
        {
            ProgressCallback.WarmupStarting => progress.OnWarmupStarting(payload.Name, payload.Total),
            ProgressCallback.WarmupCompleted => progress.OnWarmupCompleted(payload.Name),
            ProgressCallback.BenchmarkStarting =>
                progress.OnBenchmarkStarting(payload.Name, payload.Index, payload.Total),
            ProgressCallback.IterationCompleted =>
                progress.OnIterationCompleted(payload.Name, payload.Index, payload.Total),
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
    ///     Withdraws the request for a live sample stream when there is no observer to replay it
    ///     into.
    /// </summary>
    /// <remarks>
    ///     Forwarding samples costs the worker frame encoding <i>during</i> the measurement, so it is
    ///     the one thing in the protocol worth not doing speculatively. Decided here rather than at
    ///     each of the request-building call sites because this is the one place that holds both the
    ///     request and the observer the stream would be replayed into - and because it makes the rule
    ///     hold for the replicates that deliberately pass a null observer, where later workers would
    ///     otherwise pay for events the coordinator drops on the floor.
    /// </remarks>
    internal static RunGroupPayload WithStreamingForObserver(
        RunGroupPayload request,
        IMeasurementObserver observer)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Options.StreamSamples || observer != NullMeasurementObserver.Instance)
            return request;

        return request with { Options = request.Options with { StreamSamples = false } };
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
            payload.Mean,
            payload.StdDev,
            payload.CiHalfWidth,
            payload.CurrentK));

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
                errored.Add(ErroredResult(name, fault.Message));
        }

        // An unnamed fault is the group's, so every benchmark the worker never reported gets it.
        var groupFault = group.Faults.FirstOrDefault(f => f.BenchmarkName is null or "");

        if (groupFault is not null)
        {
            foreach (var expected in expectedNames)
            {
                var name = Qualify(displayPrefix, expected);

                if (reported.Add(name))
                    errored.Add(ErroredResult(name, groupFault.Message));
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
    internal static BenchmarkResult ErroredResult(string name, string message) => new()
    {
        Name = name,
        Mean = 0,
        Median = 0,
        Percentiles = [],
        Min = 0,
        Max = 0,
        StandardDeviation = 0,
        Q1 = 0,
        Q3 = 0,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 0,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
        Errored = true,
        ErrorMessage = message,
    };
}
