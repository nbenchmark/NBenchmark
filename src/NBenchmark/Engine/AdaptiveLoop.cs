using NBenchmark.Diagnostics;
using NBenchmark.Engine.Detectors;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     The single phased measurement loop. One streaming pass over the body serves as calibration,
///     warmup, and measurement: it auto-calibrates the ops-per-sample count, discards the warmup
///     prefix detected by a plateau rule, and collects measured samples until a confidence-interval
///     target is met - each dimension overridable by pinning an explicit value. The same loop backs
///     the sync and async runner paths; the dry-run short-circuit lives in the caller, so this
///     always measures.
/// </summary>
internal static class AdaptiveLoop
{
    // How many times each candidate ops-per-sample count is timed during calibration. The fastest
    // of the readings is fed to the search so one-off JIT/cold-cache spikes on a fast body can't
    // prematurely freeze K at 1. Small enough that calibration stays cheap.
    private const int CalibrationSamplesPerStep = 5;

    // A body whose first calibration probe already spans three orders of magnitude over the target
    // sample duration cannot resolve to K > 1 - the doubling search would only ever settle on the
    // very first candidate (K = 1). Running the remaining probes is pure waste (a 2 s body would
    // burn 8 s of the default 20 s budget on four extra probes that all clear the target). Once
    // the first probe clears the short-circuit factor, break out of the probe loop and feed that
    // single reading. Post-warmup K recalibration is the safety net for extreme cold-start skew
    // where the first probe is a fluke and the warm body runs much faster.
    private const int SlowBodyShortCircuitFactor = 1000;

    // Post-warmup K recalibration triggers only when the warm sample spans less than this fraction
    // of the target duration - i.e. cold calibration left K meaningfully too small. A half-target
    // threshold avoids churn from small warm/cold differences while still catching the common case
    // where the warm body runs several times faster than the tier-0 code calibration first timed.
    private const double PostWarmupTriggerFraction = 0.5;

    // Cached phase string literals for the per-sample OTLP tags. Using literals (instead of
    // MeasurementPhase.ToString()) keeps the RecordSample hot path allocation-free: the tag
    // value is a reference to an interned string, not a fresh allocation per sample.
    private const string CalibrationPhaseTag = "calibration";
    private const string WarmupPhaseTag = "warmup";
    private const string MeasurementPhaseTag = "measurement";

    public static AdaptiveResult Run(
        string name,
        Action body,
        RunSpec spec,
        IClock clock,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken ct)
    {
        var o = spec.Options;
        var autoTune = o.AutoTune;
        var measureAllocations = o.MeasureAllocations;
        var diagnostics = o.Diagnostics;
        var forceGc = o.ForceGcBeforeEachIteration;
        var maxTuningNs = autoTune.MaxTuningTime.Ticks * 100.0;
        var calibrationWarmupCapNs = maxTuningNs * autoTune.WarmupBudgetFraction;
        var graceCapNs = maxTuningNs * autoTune.CapGraceFactor;
        var calibrate = IsEligibleForCalibration(o, spec);
        var k = o.OpsPerSample ?? 1;
        int? initialOpsPerSample = null;
        double accumulatedNs = 0;
        double calibrationWarmupNs = 0;
        double bestCalibrationElapsed = 0;
        long totalBodyInvocations = 0;
        var attached = observer != NullMeasurementObserver.Instance;

        // Start the tuning-wall-clock span here, after the runner's pre-loop progress callback, so
        // the reported time covers only the adaptive loop's own work.
        var tuningStartTimestamp = clock.GetTimestamp();

        // ----- Phase 0: pre-flight jitter calibration -----
        //
        // Before any real measurement, time a deterministic busy-weight loop and derive a robust
        // jitter metric (MAD / median) of its per-sample timings. A high metric means the host is
        // under scheduling pressure (shared-tenant CI runner, thermal throttling, co-tenant noise)
        // and the IQR fence - which uses a scale estimate (IQR) with a low breakdown point - will be
        // distorted by the heavy tail. MAD's ~50% breakdown point is more resilient, so the loop
        // auto-switches the effective outlier detector when the metric exceeds the threshold.
        //
        // The probe is skipped when EnableJitterCalibration is false. Pinning OutlierMode or a
        // custom OutlierDetector disables the auto-switch but not the probe - the metric is still
        // reported for visibility.
        double? jitterMetric = null;
        var detectorSwitched = false;

        if (autoTune.EnableJitterCalibration)
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Jitter);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Jitter, PhaseTransition.Starting));

            jitterMetric = JitterCalibrator.Run(
                autoTune.JitterCalibrationSamples,
                autoTune.JitterCalibrationWorkPerSample,
                clock);

            if (ShouldSwitchDetector(o, autoTune, jitterMetric))
                detectorSwitched = true;

            NBenchmarkDiagnostics.RecordJitterMetric(jitterMetric!.Value);

            if (detectorSwitched)
                NBenchmarkDiagnostics.RecordJitterSwitch();

            NBenchmarkDiagnostics.OnPhaseCompleted(
                name, MeasurementPhase.Jitter,
                jitterMetric: jitterMetric, detectorSwitched: detectorSwitched);

            if (attached)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    name, MeasurementPhase.Jitter, PhaseTransition.Completed,
                    jitterMetric, detectorSwitched));
            }
        }

        // ----- Phase A: ops-per-sample calibration -----
        var calibrationCapped = false;
        var calibrationSamples = 0;
        var calibrationOrdinal = 0;

        if (calibrate)
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Calibration);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Calibration, PhaseTransition.Starting));

            var calibrator = new OpCountCalibrator(
                autoTune.TargetSampleDurationNs,
                Math.Min(autoTune.MaxOpsPerSample, MeasurementOptions.MaxOpsPerSampleLimit));

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var probeK = calibrator.OpsPerSample;

                // Probe each candidate K several times and feed the fastest reading. The first calls
                // into the timing path pay one-off JIT and cold-cache costs for the body *and* the
                // measurement machinery itself (GetElapsedNanoseconds reads "now" after its own JIT
                // completes, so that cost lands inside the sample), often microseconds. A single cold
                // reading would clear the target-duration check and freeze K at 1, leaving fixed timer
                // overhead unamortised so every fast body collapses onto the same floor. The minimum
                // discards those warm-up spikes (and ordinary noise) so K reflects steady state.
                var best = double.PositiveInfinity;

                for (var probe = 0; probe < CalibrationSamplesPerStep; probe++)
                {
                    var (elapsed, _, _) = AcquireSampleSync(body, spec, clock, probeK, false, forceGc, DiagnosticsOptions.None);
                    totalBodyInvocations += probeK;
                    accumulatedNs += elapsed;
                    calibrationWarmupNs += elapsed;
                    calibrationSamples++;

                    NBenchmarkDiagnostics.RecordSample(name, true, CalibrationPhaseTag, elapsed / probeK, -1);

                    if (attached)
                        observer.OnSample(new SampleEvent(name, calibrationOrdinal++, elapsed / probeK, probeK, 0, true));

                    if (elapsed < best)
                        best = elapsed;

                    // Short-circuit slow bodies: a probe that already spans many times the target
                    // sample duration cannot resolve to K > 1 (the doubling search would settle on
                    // the very first candidate anyway), and the remaining probes would just burn the
                    // tuning budget. Feed the single reading and move on.
                    if (probe == 0 && elapsed >= autoTune.TargetSampleDurationNs * SlowBodyShortCircuitFactor)
                        break;
                }

                var resolved = calibrator.Feed(best);
                k = calibrator.OpsPerSample;
                bestCalibrationElapsed = best;

                if (attached)
                    observer.OnDetector(new DetectorStateEvent(name, MeasurementPhase.Calibration, calibrationSamples, best, 0.0, 0.0, k));

                if (resolved)
                    break;

                if (calibrationWarmupNs >= calibrationWarmupCapNs)
                {
                    calibrationCapped = true;
                    break;
                }
            }

            NBenchmarkDiagnostics.OnPhaseCompleted(name, MeasurementPhase.Calibration, resolvedK: k);

            if (attached)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    name, MeasurementPhase.Calibration, PhaseTransition.Completed, ResolvedK: k));
            }
        }

        // ----- Phase B: warmup -----
        int resolvedWarmup;
        WarmupStopReason warmupStop;

        // What the warmup phase observed: whether it reached MinWarmupTime, what the JIT did while it
        // ran, and the batch-mean curve that shows tiered compilation landing. Captured on every exit
        // path so the diagnostic and the stop warnings stay honest even when warmup was cut short.
        // Pinned and calibration-capped warmup run no plateau detection, so they report no JIT signal
        // and no curve.
        var warmup = WarmupOutcome.None(timeFloorMet: true);

        if (calibrationCapped)
        {
            resolvedWarmup = calibrationSamples;
            warmupStop = WarmupStopReason.WallClockCap;
            warmup = WarmupOutcome.None(timeFloorMet: false);
        }
        else if (o.WarmupIterations is { } explicitWarmup)
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Warmup);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Warmup, PhaseTransition.Starting));

            for (var i = 0; i < explicitWarmup; i++)
            {
                ct.ThrowIfCancellationRequested();
                RunUntimedSampleSync(body, spec, k, forceGc);
                totalBodyInvocations += k;
            }

            resolvedWarmup = explicitWarmup;
            warmupStop = WarmupStopReason.ExplicitCount;
        }
        else
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Warmup);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Warmup, PhaseTransition.Starting));

            var perSampleEstimate = calibrate ? bestCalibrationElapsed : 0.0;
            var detector = new WarmupPlateauDetector(autoTune, perSampleEstimate);
            warmupStop = WarmupStopReason.Settled;
            var warmupOrdinal = 0;
            var warmupInterval = ProgressCadence(autoTune.MaxWarmup);

            var warmupSamplesFed = 0;
            JitCounters? jitBaseline = null;
            JitCounters jitLatest = default;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (elapsed, _, _) = AcquireSampleSync(body, spec, clock, k, false, forceGc, DiagnosticsOptions.None);
                totalBodyInvocations += k;
                accumulatedNs += elapsed;
                calibrationWarmupNs += elapsed;

                NBenchmarkDiagnostics.RecordSample(name, true, WarmupPhaseTag, elapsed / k, -1);

                if (attached)
                {
                    var warmupPerOp = elapsed / k;

                    if (warmupOrdinal % warmupInterval == 0)
                        observer.OnSample(new SampleEvent(name, warmupOrdinal, warmupPerOp, k, 0, true));

                    warmupOrdinal++;
                }

                // Read the process JIT compiled-method count just after the sample; the detector tracks
                // where it last changed as the JIT-quiescence gate signal. Read outside the timed window
                // (the sample's elapsed is already captured), so it never taints timing - but only at
                // batch boundaries, which is the only point the gate consults it. Warmup now spans tens
                // of thousands of samples on a fast body, and this QCall can itself allocate and trigger
                // JIT activity, perturbing the very signal it reports.
                warmupSamplesFed++;

                // At a batch boundary, snapshot all three JIT counters together: the method count
                // feeds the quiescence gate, while compilation time and IL bytes are reported as
                // warmup deltas (the first snapshot is the baseline).
                var jitCompiledCount = -1L;
                if (warmupSamplesFed % detector.EffectiveBatchSize == 0)
                {
                    var counters = JitCounters.Read();
                    jitBaseline ??= counters;
                    jitLatest = counters;
                    jitCompiledCount = counters.CompiledMethods;
                }

                if (detector.Feed(elapsed / k, elapsed, jitCompiledCount))
                {
                    warmupStop = detector.StopReason;
                    break;
                }

                if (calibrationWarmupNs >= calibrationWarmupCapNs)
                {
                    warmupStop = WarmupStopReason.WallClockCap;
                    break;
                }
            }

            resolvedWarmup = detector.Count;

            var jitDelta = jitBaseline is { } baseline ? jitLatest.Since(baseline) : default;
            warmup = new WarmupOutcome(
                TimeFloorMet: detector.TimeFloorMet,
                JitCompiledMethods: detector.JitCompiledDelta,
                JitCompilationTime: jitDelta.CompilationTime,
                JitCompiledIlBytes: jitDelta.CompiledIlBytes,
                JitLastChangeAtNs: detector.JitLastChangeAtNs,
                ElapsedNs: detector.WarmupElapsedNs,
                JitQuiescenceAchieved: detector.JitQuiescenceAchieved,
                Curve: detector.Curve,
                CurveSampleInterval: detector.CurveSampleInterval);

            // Post-warmup K recalibration: cold calibration (Phase A) resolved K against the body's
            // pre-warmup speed; the warm body may run several times faster, leaving each sample well
            // under the target duration and re-exposing the fixed timer overhead calibration existed
            // to amortise. Re-derive K from the warm per-op estimate and run one untimed sample so
            // the larger batch's cache/branch state is warm before measurement starts.
            if (calibrate && detector.LastBatchMeanPerOp > 0)
            {
                var maxOps = Math.Min(autoTune.MaxOpsPerSample, MeasurementOptions.MaxOpsPerSampleLimit);
                var recalibratedK = WarmupRecalibration.Resolve(
                    k, detector.LastBatchMeanPerOp, autoTune.TargetSampleDurationNs, maxOps, PostWarmupTriggerFraction);

                if (recalibratedK != k)
                {
                    initialOpsPerSample = k;
                    k = recalibratedK;
                    RunUntimedSampleSync(body, spec, k, forceGc);
                    totalBodyInvocations += k;

                    if (attached)
                    {
                        observer.OnDetector(new DetectorStateEvent(
                            name, MeasurementPhase.Warmup, resolvedWarmup, detector.LastBatchMeanPerOp, 0.0, 0.0, k));
                    }
                }
            }
        }

        NBenchmarkDiagnostics.OnPhaseCompleted(name, MeasurementPhase.Warmup, resolvedWarmup: resolvedWarmup, warmupStop: warmupStop);

        if (attached)
        {
            observer.OnPhase(new MeasurementPhaseEvent(
                name, MeasurementPhase.Warmup, PhaseTransition.Completed,
                ResolvedWarmup: resolvedWarmup, WarmupStop: warmupStop));
        }

        progress.OnWarmupCompleted(name).GetAwaiter().GetResult();

        // Pre-measurement full GC is intentionally profile-gated: the Independent profile clears
        // warmup garbage so it cannot trigger a collection mid-measurement, while the default
        // Realistic profile deliberately inherits the warmup heap to match production. This is a
        // distinct decision from the between-benchmark GC (ForceGcBetweenBenchmarks, run by
        // SuiteRunner), which is on for both profiles.
        if (o.ForceGcBeforeMeasurement)
            GcControl.ForceFullGc();

        // ----- Phase C: measurement -----
        var explicitSamples = o.Iterations;

        // The time-derived sample floor (see MeasurementGates) resolves the count a cheap body is held
        // to, so size the list for that rather than for MinSamples - otherwise every auto run regrows
        // the backing array several times on the hot path.
        var sampleCeiling = MeasurementGates.ResolveTimeFloorCeiling(autoTune.MinSamples, autoTune.MaxSamples);
        var minMeasurementNs = autoTune.MinMeasurementTime.Ticks * 100.0;
        var timings = new List<double>(explicitSamples ?? sampleCeiling);
        var allocations = measureAllocations ? new List<long>(timings.Capacity) : null;
        var diagnosticsList = diagnostics.Any ? new List<DiagnosticDelta>(timings.Capacity) : null;
        var ci = explicitSamples is null ? new CiWidthDetector(o.ConfidenceLevel, autoTune) : null;
        var tracker = new SplitHalfTracker(timings);
        double measurementNs = 0;
        var restarts = 0;

        // Subscribe to FirstChanceException for the measurement phase only.
        if (diagnostics.Exceptions)
            ExceptionCounter.Subscribe();

        // Capture heap info snapshot before measurement.
        HeapSnapshot? heapInfo = null;

        if (diagnostics.GcHeapInfo)
        {
            var info = GC.GetGCMemoryInfo();
            heapInfo = new HeapSnapshot(info.HeapSizeBytes, info.FragmentedBytes);
        }

        // Capture exception count before measurement.
        var exceptionCountBefore = diagnostics.Exceptions ? ExceptionCounter.Capture() : 0L;

        // In auto mode the sample count is resolved at runtime, so there is no honest total to
        // report - signal indeterminate (0) to the progress UI while still bounding how often we
        // notify by the ceiling.
        var reportedTotal = explicitSamples ?? 0;
        var progressInterval = ProgressCadence(explicitSamples ?? autoTune.MaxSamples);

        NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Measurement);

        if (attached)
            observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Measurement, PhaseTransition.Starting));

        var measureStartTimestamp = clock.GetTimestamp();
        var sampleCount = 0;
        SampleStopReason sampleStop;
        var detectorEmitted = false;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (elapsed, allocDelta, diagDelta) = AcquireSampleSync(body, spec, clock, k, measureAllocations, forceGc, diagnostics);
                totalBodyInvocations += k;
                accumulatedNs += elapsed;
                measurementNs += elapsed;
                var perOp = elapsed / k;
                timings.Add(perOp);
                allocations?.Add(allocDelta / k);
                diagnosticsList?.Add(diagDelta);
                tracker.Add(perOp);
                sampleCount++;

                NBenchmarkDiagnostics.RecordSample(name, false, MeasurementPhaseTag, perOp, measureAllocations ? allocDelta / k : -1L);

                if (attached)
                {
                    var allocPerOp = measureAllocations ? allocDelta / k : 0L;

                    if (sampleCount % progressInterval == 0)
                        observer.OnSample(new SampleEvent(name, sampleCount - 1, perOp, k, allocPerOp, false));
                }

                if (sampleCount % progressInterval == 0)
                    progress.OnIterationCompleted(name, sampleCount, reportedTotal).GetAwaiter().GetResult();

                if (ci is null)
                {
                    // A null CI detector means the count is pinned (the detector is built only in auto
                    // mode), so explicitSamples is set; stop once the pinned target is reached.
                    if (sampleCount >= explicitSamples.GetValueOrDefault())
                    {
                        sampleStop = SampleStopReason.ExplicitCount;
                        break;
                    }
                }
                else if (ci.Feed(perOp, MeasurementGates.TimeFloorMet(sampleCount, measurementNs, minMeasurementNs, sampleCeiling)))
                {
                    // Steady-state gate on the CI stop, the measurement-phase counterpart to the warmup
                    // settle gates. A step change inside the measurement window (a JIT tier-up, a PGO
                    // re-optimization) leaves the CI rule perfectly willing to report a tight interval
                    // straight across it, which is how a 10x-wrong number gets a +/-0.9% error bar.
                    if (ci.StopReason == SampleStopReason.CiTargetMet
                        && !MeasurementGates.IsSteady(
                            tracker.FirstHalfMean, tracker.SecondHalfMean, sampleCount, ci.StandardDeviation,
                            autoTune.MeasurementDriftTolerance, MeasurementGates.DefaultSigmaTolerance))
                    {
                        if (restarts < autoTune.MeasurementRestartLimit)
                        {
                            restarts++;

                            // Discard the whole prefix, not just the stale half: after a detected step
                            // there is no way to know where the new regime began, so the later samples
                            // may themselves be mid-transition. All three per-sample lists are
                            // index-aligned (the GC-correlation annotation reads diagnostics by
                            // ordinal against timings), so they must be cleared together.
                            timings.Clear();
                            allocations?.Clear();
                            diagnosticsList?.Clear();
                            tracker.Reset();
                            sampleCount = 0;
                            measurementNs = 0;
                            ci = new CiWidthDetector(o.ConfidenceLevel, autoTune);

                            // Re-announce the phase so observers and the progress UI see an explicit
                            // ordinal reset rather than a silent rewind.
                            if (attached)
                            {
                                observer.OnPhase(new MeasurementPhaseEvent(
                                    name, MeasurementPhase.Measurement, PhaseTransition.Starting));
                            }

                            // accumulatedNs is deliberately NOT reset: restarts draw on the same tuning
                            // budget as ordinary sampling, so they can never extend total runtime.
                            continue;
                        }

                        sampleStop = SampleStopReason.DriftUnresolved;
                        break;
                    }

                    sampleStop = ci.StopReason;

                    NBenchmarkDiagnostics.RecordDetectorState(ci.AchievedRelativeHalfWidth, ci.Mean);

                    if (attached)
                    {
                        observer.OnDetector(new DetectorStateEvent(
                            name, MeasurementPhase.Measurement, (int)ci.Count, ci.Mean, ci.StandardDeviation,
                            ci.AchievedRelativeHalfWidth, k));

                        detectorEmitted = true;
                    }

                    break;
                }

                if (accumulatedNs >= maxTuningNs)
                {
                    var underMinSamples = sampleCount < autoTune.MinSamples;
                    var graceEnabled = graceCapNs > maxTuningNs;

                    // Base cap fired below MinSamples with grace budget left: keep sampling rather
                    // than stop on a dangerously under-sampled result (a one-sample stop reports
                    // StdDev = 0 and a zero error margin - clean-looking but meaningless).
                    if (underMinSamples && graceEnabled && accumulatedNs < graceCapNs)
                        continue;

                    // Grace exhausted while still under MinSamples flags the result unreliable;
                    // otherwise this is an ordinary cap stop (enough samples, or grace disabled).
                    sampleStop = underMinSamples && graceEnabled
                        ? SampleStopReason.GraceCapExhausted
                        : SampleStopReason.WallClockCap;
                    break;
                }
            }
        }
        finally
        {
            if (diagnostics.Exceptions)
                ExceptionCounter.Unsubscribe();
        }

        if (ci is not null && !detectorEmitted)
            NBenchmarkDiagnostics.RecordDetectorState(ci.AchievedRelativeHalfWidth, ci.Mean);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            name, MeasurementPhase.Measurement,
            sampleStop,
            achievedCiWidth: ci?.AchievedRelativeHalfWidth,
            ciTarget: autoTune.CiTarget);

        if (attached)
        {
            if (ci is not null && !detectorEmitted)
            {
                observer.OnDetector(new DetectorStateEvent(
                    name, MeasurementPhase.Measurement, (int)ci.Count, ci.Mean, ci.StandardDeviation,
                    ci.AchievedRelativeHalfWidth, k));
            }

            observer.OnPhase(new MeasurementPhaseEvent(
                name, MeasurementPhase.Measurement, PhaseTransition.Completed, SampleStop: sampleStop));
        }

        var measuredDuration = clock.GetElapsedTime(measureStartTimestamp);

        // Capture final exception count after the measurement loop (unsubscribe ran in finally).
        long? exceptionCount = null;

        if (diagnostics.Exceptions)
            exceptionCount = ExceptionCounter.Delta(exceptionCountBefore);

        // Capture heap info snapshot after measurement.
        if (diagnostics.GcHeapInfo)
        {
            var info = GC.GetGCMemoryInfo();
            heapInfo = new HeapSnapshot(info.HeapSizeBytes - heapInfo?.CommittedBytes ?? 0, info.FragmentedBytes - heapInfo?.FragmentedBytes ?? 0);
        }

        return BuildResult(
            timings, allocations, diagnosticsList, exceptionCount, heapInfo, measuredDuration, resolvedWarmup, sampleCount, k,
            totalBodyInvocations, warmupStop, sampleStop, o.ConfidenceLevel,
            clock.GetElapsedTime(tuningStartTimestamp),
            calibrationCapped,
            autoTune.MaxTuningTime,
            autoTune.CiTarget,
            autoTune.MaxSamples,
            explicitSamples,
            jitterMetric,
            detectorSwitched,
            autoTune.CapGraceFactor,
            autoTune.WarmupBudgetFraction,
            initialOpsPerSample,
            ciWidthSeries: ci?.HalfWidthSeries ?? [],
            warmup: warmup,
            measurementRestarts: restarts,
            // Computed unconditionally, not only on the drift-gate path, so drift stays visible on
            // pinned-count, wall-clock-cap, and ceiling stops - none of which consult the gate.
            splitHalfDrift: MeasurementGates.SplitHalfDrift(tracker.FirstHalfMean, tracker.SecondHalfMean),
            coefficientOfVariation: ci?.CoefficientOfVariation ?? double.NaN,
            hasIterationHooks: spec.IterationSetup is not null || spec.IterationTeardown is not null);
    }

    public static async Task<AdaptiveResult> RunAsync(
        string name,
        Func<Task> body,
        RunSpec spec,
        IClock clock,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        CancellationToken ct)
    {
        var o = spec.Options;
        var autoTune = o.AutoTune;
        var measureAllocations = o.MeasureAllocations;
        var diagnostics = o.Diagnostics;
        var forceGc = o.ForceGcBeforeEachIteration;
        var maxTuningNs = autoTune.MaxTuningTime.Ticks * 100.0;
        var calibrationWarmupCapNs = maxTuningNs * autoTune.WarmupBudgetFraction;
        var graceCapNs = maxTuningNs * autoTune.CapGraceFactor;
        var calibrate = IsEligibleForCalibration(o, spec);
        var k = o.OpsPerSample ?? 1;
        int? initialOpsPerSample = null;
        double accumulatedNs = 0;
        double calibrationWarmupNs = 0;
        double bestCalibrationElapsed = 0;
        long totalBodyInvocations = 0;
        var attached = observer != NullMeasurementObserver.Instance;

        // Start the tuning-wall-clock span here, after the runner's pre-loop progress callback, so
        // the reported time covers only the adaptive loop's own work.
        var tuningStartTimestamp = clock.GetTimestamp();

        // ----- Phase 0: pre-flight jitter calibration -----
        //
        // Before any real measurement, time a deterministic busy-weight loop and derive a robust
        // jitter metric (MAD / median) of its per-sample timings. A high metric means the host is
        // under scheduling pressure (shared-tenant CI runner, thermal throttling, co-tenant noise)
        // and the IQR fence - which uses a scale estimate (IQR) with a low breakdown point - will be
        // distorted by the heavy tail. MAD's ~50% breakdown point is more resilient, so the loop
        // auto-switches the effective outlier detector when the metric exceeds the threshold.
        //
        // The probe is skipped when EnableJitterCalibration is false. Pinning OutlierMode or a
        // custom OutlierDetector disables the auto-switch but not the probe - the metric is still
        // reported for visibility.
        double? jitterMetric = null;
        var detectorSwitched = false;

        if (autoTune.EnableJitterCalibration)
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Jitter);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Jitter, PhaseTransition.Starting));

            jitterMetric = JitterCalibrator.Run(
                autoTune.JitterCalibrationSamples,
                autoTune.JitterCalibrationWorkPerSample,
                clock);

            if (ShouldSwitchDetector(o, autoTune, jitterMetric))
                detectorSwitched = true;

            NBenchmarkDiagnostics.RecordJitterMetric(jitterMetric!.Value);

            if (detectorSwitched)
                NBenchmarkDiagnostics.RecordJitterSwitch();

            NBenchmarkDiagnostics.OnPhaseCompleted(
                name, MeasurementPhase.Jitter,
                jitterMetric: jitterMetric, detectorSwitched: detectorSwitched);

            if (attached)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    name, MeasurementPhase.Jitter, PhaseTransition.Completed,
                    jitterMetric, detectorSwitched));
            }
        }

        // ----- Phase A: ops-per-sample calibration -----
        var calibrationCapped = false;
        var calibrationSamples = 0;
        var calibrationOrdinal = 0;

        if (calibrate)
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Calibration);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Calibration, PhaseTransition.Starting));

            var calibrator = new OpCountCalibrator(
                autoTune.TargetSampleDurationNs,
                Math.Min(autoTune.MaxOpsPerSample, MeasurementOptions.MaxOpsPerSampleLimit));

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var probeK = calibrator.OpsPerSample;

                // Probe each candidate K several times and feed the fastest reading. The first calls
                // into the timing path pay one-off JIT and cold-cache costs for the body *and* the
                // measurement machinery itself (GetElapsedNanoseconds reads "now" after its own JIT
                // completes, so that cost lands inside the sample), often microseconds. A single cold
                // reading would clear the target-duration check and freeze K at 1, leaving fixed timer
                // overhead unamortised so every fast body collapses onto the same floor. The minimum
                // discards those warm-up spikes (and ordinary noise) so K reflects steady state.
                var best = double.PositiveInfinity;

                for (var probe = 0; probe < CalibrationSamplesPerStep; probe++)
                {
                    var (elapsed, _, _) = await AcquireSampleAsync(body, spec, clock, probeK, false, forceGc, DiagnosticsOptions.None)
                        .ConfigureAwait(false);

                    totalBodyInvocations += probeK;
                    accumulatedNs += elapsed;
                    calibrationWarmupNs += elapsed;
                    calibrationSamples++;

                    NBenchmarkDiagnostics.RecordSample(name, true, CalibrationPhaseTag, elapsed / probeK, -1);

                    if (attached)
                        observer.OnSample(new SampleEvent(name, calibrationOrdinal++, elapsed / probeK, probeK, 0, true));

                    if (elapsed < best)
                        best = elapsed;

                    // Short-circuit slow bodies: a probe that already spans many times the target
                    // sample duration cannot resolve to K > 1 (the doubling search would settle on
                    // the very first candidate anyway), and the remaining probes would just burn the
                    // tuning budget. Feed the single reading and move on.
                    if (probe == 0 && elapsed >= autoTune.TargetSampleDurationNs * SlowBodyShortCircuitFactor)
                        break;
                }

                var resolved = calibrator.Feed(best);
                k = calibrator.OpsPerSample;
                bestCalibrationElapsed = best;

                if (attached)
                    observer.OnDetector(new DetectorStateEvent(name, MeasurementPhase.Calibration, calibrationSamples, best, 0.0, 0.0, k));

                if (resolved)
                    break;

                if (calibrationWarmupNs >= calibrationWarmupCapNs)
                {
                    calibrationCapped = true;
                    break;
                }
            }

            NBenchmarkDiagnostics.OnPhaseCompleted(name, MeasurementPhase.Calibration, resolvedK: k);

            if (attached)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    name, MeasurementPhase.Calibration, PhaseTransition.Completed, ResolvedK: k));
            }
        }

        // ----- Phase B: warmup -----
        int resolvedWarmup;
        WarmupStopReason warmupStop;

        // What the warmup phase observed: whether it reached MinWarmupTime, what the JIT did while it
        // ran, and the batch-mean curve that shows tiered compilation landing. Captured on every exit
        // path so the diagnostic and the stop warnings stay honest even when warmup was cut short.
        // Pinned and calibration-capped warmup run no plateau detection, so they report no JIT signal
        // and no curve.
        var warmup = WarmupOutcome.None(timeFloorMet: true);

        if (calibrationCapped)
        {
            resolvedWarmup = calibrationSamples;
            warmupStop = WarmupStopReason.WallClockCap;
            warmup = WarmupOutcome.None(timeFloorMet: false);
        }
        else if (o.WarmupIterations is { } explicitWarmup)
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Warmup);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Warmup, PhaseTransition.Starting));

            for (var i = 0; i < explicitWarmup; i++)
            {
                ct.ThrowIfCancellationRequested();
                await RunUntimedSampleAsync(body, spec, k, forceGc).ConfigureAwait(false);
                totalBodyInvocations += k;
            }

            resolvedWarmup = explicitWarmup;
            warmupStop = WarmupStopReason.ExplicitCount;
        }
        else
        {
            NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Warmup);

            if (attached)
                observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Warmup, PhaseTransition.Starting));

            var perSampleEstimate = calibrate ? bestCalibrationElapsed : 0.0;
            var detector = new WarmupPlateauDetector(autoTune, perSampleEstimate);
            warmupStop = WarmupStopReason.Settled;
            var warmupOrdinal = 0;
            var warmupInterval = ProgressCadence(autoTune.MaxWarmup);

            var warmupSamplesFed = 0;
            JitCounters? jitBaseline = null;
            JitCounters jitLatest = default;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (elapsed, _, _) = await AcquireSampleAsync(body, spec, clock, k, false, forceGc, DiagnosticsOptions.None).ConfigureAwait(false);
                totalBodyInvocations += k;
                accumulatedNs += elapsed;
                calibrationWarmupNs += elapsed;

                NBenchmarkDiagnostics.RecordSample(name, true, WarmupPhaseTag, elapsed / k, -1);

                if (attached)
                {
                    var warmupPerOp = elapsed / k;

                    if (warmupOrdinal % warmupInterval == 0)
                        observer.OnSample(new SampleEvent(name, warmupOrdinal, warmupPerOp, k, 0, true));

                    warmupOrdinal++;
                }

                // Read the process JIT compiled-method count just after the sample; the detector tracks
                // where it last changed as the JIT-quiescence gate signal. Read outside the timed window
                // (the sample's elapsed is already captured), so it never taints timing - but only at
                // batch boundaries, which is the only point the gate consults it. Warmup now spans tens
                // of thousands of samples on a fast body, and this QCall can itself allocate and trigger
                // JIT activity, perturbing the very signal it reports.
                warmupSamplesFed++;

                // At a batch boundary, snapshot all three JIT counters together: the method count
                // feeds the quiescence gate, while compilation time and IL bytes are reported as
                // warmup deltas (the first snapshot is the baseline).
                var jitCompiledCount = -1L;
                if (warmupSamplesFed % detector.EffectiveBatchSize == 0)
                {
                    var counters = JitCounters.Read();
                    jitBaseline ??= counters;
                    jitLatest = counters;
                    jitCompiledCount = counters.CompiledMethods;
                }

                if (detector.Feed(elapsed / k, elapsed, jitCompiledCount))
                {
                    warmupStop = detector.StopReason;
                    break;
                }

                if (calibrationWarmupNs >= calibrationWarmupCapNs)
                {
                    warmupStop = WarmupStopReason.WallClockCap;
                    break;
                }
            }

            resolvedWarmup = detector.Count;

            var jitDelta = jitBaseline is { } baseline ? jitLatest.Since(baseline) : default;
            warmup = new WarmupOutcome(
                TimeFloorMet: detector.TimeFloorMet,
                JitCompiledMethods: detector.JitCompiledDelta,
                JitCompilationTime: jitDelta.CompilationTime,
                JitCompiledIlBytes: jitDelta.CompiledIlBytes,
                JitLastChangeAtNs: detector.JitLastChangeAtNs,
                ElapsedNs: detector.WarmupElapsedNs,
                JitQuiescenceAchieved: detector.JitQuiescenceAchieved,
                Curve: detector.Curve,
                CurveSampleInterval: detector.CurveSampleInterval);

            // Post-warmup K recalibration: cold calibration (Phase A) resolved K against the body's
            // pre-warmup speed; the warm body may run several times faster, leaving each sample well
            // under the target duration and re-exposing the fixed timer overhead calibration existed
            // to amortise. Re-derive K from the warm per-op estimate and run one untimed sample so
            // the larger batch's cache/branch state is warm before measurement starts.
            if (calibrate && detector.LastBatchMeanPerOp > 0)
            {
                var maxOps = Math.Min(autoTune.MaxOpsPerSample, MeasurementOptions.MaxOpsPerSampleLimit);
                var recalibratedK = WarmupRecalibration.Resolve(
                    k, detector.LastBatchMeanPerOp, autoTune.TargetSampleDurationNs, maxOps, PostWarmupTriggerFraction);

                if (recalibratedK != k)
                {
                    initialOpsPerSample = k;
                    k = recalibratedK;
                    await RunUntimedSampleAsync(body, spec, k, forceGc).ConfigureAwait(false);
                    totalBodyInvocations += k;

                    if (attached)
                    {
                        observer.OnDetector(new DetectorStateEvent(
                            name, MeasurementPhase.Warmup, resolvedWarmup, detector.LastBatchMeanPerOp, 0.0, 0.0, k));
                    }
                }
            }
        }

        NBenchmarkDiagnostics.OnPhaseCompleted(name, MeasurementPhase.Warmup, resolvedWarmup: resolvedWarmup, warmupStop: warmupStop);

        if (attached)
        {
            observer.OnPhase(new MeasurementPhaseEvent(
                name, MeasurementPhase.Warmup, PhaseTransition.Completed,
                ResolvedWarmup: resolvedWarmup, WarmupStop: warmupStop));
        }

        await progress.OnWarmupCompleted(name).ConfigureAwait(false);

        // Pre-measurement full GC is intentionally profile-gated: the Independent profile clears
        // warmup garbage so it cannot trigger a collection mid-measurement, while the default
        // Realistic profile deliberately inherits the warmup heap to match production. This is a
        // distinct decision from the between-benchmark GC (ForceGcBetweenBenchmarks, run by
        // SuiteRunner), which is on for both profiles.
        if (o.ForceGcBeforeMeasurement)
            GcControl.ForceFullGc();

        // ----- Phase C: measurement -----
        var explicitSamples = o.Iterations;

        // The time-derived sample floor (see MeasurementGates) resolves the count a cheap body is held
        // to, so size the list for that rather than for MinSamples - otherwise every auto run regrows
        // the backing array several times on the hot path.
        var sampleCeiling = MeasurementGates.ResolveTimeFloorCeiling(autoTune.MinSamples, autoTune.MaxSamples);
        var minMeasurementNs = autoTune.MinMeasurementTime.Ticks * 100.0;
        var timings = new List<double>(explicitSamples ?? sampleCeiling);
        var allocations = measureAllocations ? new List<long>(timings.Capacity) : null;
        var diagnosticsList = diagnostics.Any ? new List<DiagnosticDelta>(timings.Capacity) : null;
        var ci = explicitSamples is null ? new CiWidthDetector(o.ConfidenceLevel, autoTune) : null;
        var tracker = new SplitHalfTracker(timings);
        double measurementNs = 0;
        var restarts = 0;

        // Subscribe to FirstChanceException for the measurement phase only.
        if (diagnostics.Exceptions)
            ExceptionCounter.Subscribe();

        // Capture heap info snapshot before measurement.
        HeapSnapshot? heapInfo = null;

        if (diagnostics.GcHeapInfo)
        {
            var info = GC.GetGCMemoryInfo();
            heapInfo = new HeapSnapshot(info.HeapSizeBytes, info.FragmentedBytes);
        }

        // Capture exception count before measurement.
        var exceptionCountBefore = diagnostics.Exceptions ? ExceptionCounter.Capture() : 0L;

        // In auto mode the sample count is resolved at runtime, so there is no honest total to
        // report - signal indeterminate (0) to the progress UI while still bounding how often we
        // notify by the ceiling.
        var reportedTotal = explicitSamples ?? 0;
        var progressInterval = ProgressCadence(explicitSamples ?? autoTune.MaxSamples);

        NBenchmarkDiagnostics.OnPhaseStarting(name, MeasurementPhase.Measurement);

        if (attached)
            observer.OnPhase(new MeasurementPhaseEvent(name, MeasurementPhase.Measurement, PhaseTransition.Starting));

        var measureStartTimestamp = clock.GetTimestamp();
        var sampleCount = 0;
        SampleStopReason sampleStop;
        var detectorEmitted = false;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var (elapsed, allocDelta, diagDelta) = await AcquireSampleAsync(body, spec, clock, k, measureAllocations, forceGc, diagnostics)
                    .ConfigureAwait(false);

                totalBodyInvocations += k;
                accumulatedNs += elapsed;
                measurementNs += elapsed;
                var perOp = elapsed / k;
                timings.Add(perOp);
                allocations?.Add(allocDelta / k);
                diagnosticsList?.Add(diagDelta);
                tracker.Add(perOp);
                sampleCount++;

                NBenchmarkDiagnostics.RecordSample(name, false, MeasurementPhaseTag, perOp, measureAllocations ? allocDelta / k : -1L);

                if (attached)
                {
                    var allocPerOp = measureAllocations ? allocDelta / k : 0L;

                    if (sampleCount % progressInterval == 0)
                        observer.OnSample(new SampleEvent(name, sampleCount - 1, perOp, k, allocPerOp, false));
                }

                if (sampleCount % progressInterval == 0)
                    await progress.OnIterationCompleted(name, sampleCount, reportedTotal).ConfigureAwait(false);

                if (ci is null)
                {
                    // A null CI detector means the count is pinned (the detector is built only in auto
                    // mode), so explicitSamples is set; stop once the pinned target is reached.
                    if (sampleCount >= explicitSamples.GetValueOrDefault())
                    {
                        sampleStop = SampleStopReason.ExplicitCount;
                        break;
                    }
                }
                else if (ci.Feed(perOp, MeasurementGates.TimeFloorMet(sampleCount, measurementNs, minMeasurementNs, sampleCeiling)))
                {
                    // Steady-state gate on the CI stop, the measurement-phase counterpart to the warmup
                    // settle gates. A step change inside the measurement window (a JIT tier-up, a PGO
                    // re-optimization) leaves the CI rule perfectly willing to report a tight interval
                    // straight across it, which is how a 10x-wrong number gets a +/-0.9% error bar.
                    if (ci.StopReason == SampleStopReason.CiTargetMet
                        && !MeasurementGates.IsSteady(
                            tracker.FirstHalfMean, tracker.SecondHalfMean, sampleCount, ci.StandardDeviation,
                            autoTune.MeasurementDriftTolerance, MeasurementGates.DefaultSigmaTolerance))
                    {
                        if (restarts < autoTune.MeasurementRestartLimit)
                        {
                            restarts++;

                            // Discard the whole prefix, not just the stale half: after a detected step
                            // there is no way to know where the new regime began, so the later samples
                            // may themselves be mid-transition. All three per-sample lists are
                            // index-aligned (the GC-correlation annotation reads diagnostics by
                            // ordinal against timings), so they must be cleared together.
                            timings.Clear();
                            allocations?.Clear();
                            diagnosticsList?.Clear();
                            tracker.Reset();
                            sampleCount = 0;
                            measurementNs = 0;
                            ci = new CiWidthDetector(o.ConfidenceLevel, autoTune);

                            // Re-announce the phase so observers and the progress UI see an explicit
                            // ordinal reset rather than a silent rewind.
                            if (attached)
                            {
                                observer.OnPhase(new MeasurementPhaseEvent(
                                    name, MeasurementPhase.Measurement, PhaseTransition.Starting));
                            }

                            // accumulatedNs is deliberately NOT reset: restarts draw on the same tuning
                            // budget as ordinary sampling, so they can never extend total runtime.
                            continue;
                        }

                        sampleStop = SampleStopReason.DriftUnresolved;
                        break;
                    }

                    sampleStop = ci.StopReason;

                    NBenchmarkDiagnostics.RecordDetectorState(ci.AchievedRelativeHalfWidth, ci.Mean);

                    if (attached)
                    {
                        observer.OnDetector(new DetectorStateEvent(
                            name, MeasurementPhase.Measurement, (int)ci.Count, ci.Mean, ci.StandardDeviation,
                            ci.AchievedRelativeHalfWidth, k));

                        detectorEmitted = true;
                    }

                    break;
                }

                if (accumulatedNs >= maxTuningNs)
                {
                    var underMinSamples = sampleCount < autoTune.MinSamples;
                    var graceEnabled = graceCapNs > maxTuningNs;

                    // Base cap fired below MinSamples with grace budget left: keep sampling rather
                    // than stop on a dangerously under-sampled result (a one-sample stop reports
                    // StdDev = 0 and a zero error margin - clean-looking but meaningless).
                    if (underMinSamples && graceEnabled && accumulatedNs < graceCapNs)
                        continue;

                    // Grace exhausted while still under MinSamples flags the result unreliable;
                    // otherwise this is an ordinary cap stop (enough samples, or grace disabled).
                    sampleStop = underMinSamples && graceEnabled
                        ? SampleStopReason.GraceCapExhausted
                        : SampleStopReason.WallClockCap;
                    break;
                }
            }
        }
        finally
        {
            if (diagnostics.Exceptions)
                ExceptionCounter.Unsubscribe();
        }

        if (ci is not null && !detectorEmitted)
            NBenchmarkDiagnostics.RecordDetectorState(ci.AchievedRelativeHalfWidth, ci.Mean);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            name, MeasurementPhase.Measurement,
            sampleStop,
            achievedCiWidth: ci?.AchievedRelativeHalfWidth,
            ciTarget: autoTune.CiTarget);

        if (attached)
        {
            if (ci is not null && !detectorEmitted)
            {
                observer.OnDetector(new DetectorStateEvent(
                    name, MeasurementPhase.Measurement, (int)ci.Count, ci.Mean, ci.StandardDeviation,
                    ci.AchievedRelativeHalfWidth, k));
            }

            observer.OnPhase(new MeasurementPhaseEvent(
                name, MeasurementPhase.Measurement, PhaseTransition.Completed, SampleStop: sampleStop));
        }

        var measuredDuration = clock.GetElapsedTime(measureStartTimestamp);

        // Capture final exception count after the measurement loop (unsubscribe ran in finally).
        long? exceptionCount = null;

        if (diagnostics.Exceptions)
            exceptionCount = ExceptionCounter.Delta(exceptionCountBefore);

        // Capture heap info snapshot after measurement.
        if (diagnostics.GcHeapInfo)
        {
            var info = GC.GetGCMemoryInfo();
            heapInfo = new HeapSnapshot(info.HeapSizeBytes - heapInfo?.CommittedBytes ?? 0, info.FragmentedBytes - heapInfo?.FragmentedBytes ?? 0);
        }

        return BuildResult(
            timings, allocations, diagnosticsList, exceptionCount, heapInfo, measuredDuration, resolvedWarmup, sampleCount, k,
            totalBodyInvocations, warmupStop, sampleStop, o.ConfidenceLevel,
            clock.GetElapsedTime(tuningStartTimestamp),
            calibrationCapped,
            autoTune.MaxTuningTime,
            autoTune.CiTarget,
            autoTune.MaxSamples,
            explicitSamples,
            jitterMetric,
            detectorSwitched,
            autoTune.CapGraceFactor,
            autoTune.WarmupBudgetFraction,
            initialOpsPerSample,
            ciWidthSeries: ci?.HalfWidthSeries ?? [],
            warmup: warmup,
            measurementRestarts: restarts,
            // Computed unconditionally, not only on the drift-gate path, so drift stays visible on
            // pinned-count, wall-clock-cap, and ceiling stops - none of which consult the gate.
            splitHalfDrift: MeasurementGates.SplitHalfDrift(tracker.FirstHalfMean, tracker.SecondHalfMean),
            coefficientOfVariation: ci?.CoefficientOfVariation ?? double.NaN,
            hasIterationHooks: spec.IterationSetup is not null || spec.IterationTeardown is not null);
    }

    // Per-iteration setup/teardown make a K-batch semantically wrong (each op must be paired with
    // its own setup), so those still block calibration. ForceGcBeforeEachIteration does NOT: the
    // forced Gen0 collection runs once per sample (K-batch), before the timestamp and outside the
    // timed window, which is exactly the pinned-K semantics - so auto-K under the Independent
    // profile behaves identically to a user who pinned K there.
    private static bool IsEligibleForCalibration(MeasurementOptions o, RunSpec spec)
        => o.OpsPerSample is null
           && spec.IterationSetup is null
           && spec.IterationTeardown is null;

    private static AdaptiveResult BuildResult(
        List<double> timings,
        List<long>? allocations,
        List<DiagnosticDelta>? diagnosticsList,
        long? exceptionCount,
        HeapSnapshot? heapInfo,
        TimeSpan measuredDuration,
        int resolvedWarmup,
        int sampleCount,
        int opsPerSample,
        long totalBodyInvocations,
        WarmupStopReason warmupStop,
        SampleStopReason sampleStop,
        double confidenceLevel,
        TimeSpan tuningWallClock,
        bool calibrationCapped,
        TimeSpan maxTuningTime,
        double ciTarget,
        int maxSamples,
        int? explicitSamples,
        double? jitterMetric,
        bool detectorSwitched,
        double capGraceFactor,
        double warmupBudgetFraction,
        int? initialOpsPerSample,
        IReadOnlyList<double> ciWidthSeries,
        WarmupOutcome warmup,
        int measurementRestarts,
        double splitHalfDrift,
        double coefficientOfVariation,
        bool hasIterationHooks)
    {
        var timingsArray = timings.ToArray();

        // Achieved raw CI half-width, computed the same way for explicit and auto modes so the
        // diagnostic is consistent. The reported interval is computed separately on trimmed samples.
        var rawStats = StatsSummary.Compute(timingsArray, confidenceLevel);
        var achievedCi = rawStats.Mean > 0 ? rawStats.MarginOfError / rawStats.Mean : 0.0;

        var diagnostic = new AutoTuneDiagnostic
        {
            ResolvedWarmup = resolvedWarmup,
            ResolvedSamples = sampleCount,
            OpsPerSample = opsPerSample,
            InitialOpsPerSample = initialOpsPerSample,
            TotalBodyInvocations = totalBodyInvocations,
            WarmupStop = warmupStop,
            SampleStop = sampleStop,
            AchievedRelativeCiWidth = achievedCi,
            TuningWallClock = tuningWallClock,
            JitterMetric = jitterMetric,
            OutlierDetectorSwitched = detectorSwitched,
            CiWidthSeries = ciWidthSeries,
            WarmupTimeFloorMet = warmup.TimeFloorMet,
            WarmupJitCompiledMethods = warmup.JitCompiledMethods,
            WarmupJitCompilationTime = warmup.JitCompilationTime,
            WarmupJitCompiledIlBytes = warmup.JitCompiledIlBytes,
            JitLastChangeAtNs = warmup.JitLastChangeAtNs,
            WarmupElapsedNs = warmup.ElapsedNs,
            JitQuiescenceAchieved = warmup.JitQuiescenceAchieved,
            WarmupCurve = warmup.Curve,
            WarmupSampleInterval = warmup.CurveSampleInterval,
            MeasurementRestarts = measurementRestarts,
            SplitHalfDrift = splitHalfDrift,
        };

        var warnings = BuildStopWarnings(
            warmupStop, sampleStop, calibrationCapped, maxTuningTime,
            achievedCi, ciTarget, maxSamples, sampleCount, explicitSamples, capGraceFactor,
            warmupBudgetFraction, warmup.TimeFloorMet, measurementRestarts, splitHalfDrift,
            coefficientOfVariation, hasIterationHooks, resolvedWarmup);

        if (detectorSwitched)
        {
            var switchWarning = BuildJitterSwitchWarning(jitterMetric);
            warnings = warnings.Count == 0 ? switchWarning : [..warnings, ..switchWarning];
        }

        // The effective detector is non-null only when the loop auto-switched it; the caller
        // (BenchmarkRunner) inspects this to build an effective options record for the stats
        // pipeline. When no switch happened, the caller uses the options' configured detector.
        var effectiveDetector = detectorSwitched
            ? OutlierDetectors.MedianAbsoluteDeviation
            : null;

        return new AdaptiveResult(
            timingsArray,
            allocations?.ToArray(),
            diagnosticsList?.ToArray(),
            exceptionCount,
            heapInfo,
            measuredDuration,
            resolvedWarmup,
            diagnostic,
            warnings,
            effectiveDetector);
    }

    /// <summary>
    ///     Decides whether the loop should auto-switch the outlier detector from the configured
    ///     <c>IqrFence</c> to <c>MedianAbsoluteDeviation</c> based on the jitter metric. The switch
    ///     fires only when all of the following hold:
    ///     <list type="bullet">
    ///         <item>The jitter metric is a finite, positive value (the probe produced usable data).</item>
    ///         <item>
    ///             <see cref="AutoTuneOptions.JitterAutoSwitchThreshold" /> is positive (a non-positive
    ///             value disables the auto-switch while keeping the probe).
    ///         </item>
    ///         <item>The metric exceeds the threshold.</item>
    ///         <item>The user has not pinned a custom <see cref="MeasurementOptions.OutlierDetector" />.</item>
    ///         <item>
    ///             The configured <see cref="MeasurementOptions.OutlierMode" /> is the default
    ///             <c>IqrFence</c> - switching from any other explicitly-chosen mode would override user
    ///             intent.
    ///         </item>
    ///     </list>
    /// </summary>
    private static bool ShouldSwitchDetector(MeasurementOptions o, AutoTuneOptions autoTune, double? jitterMetric)
    {
        if (jitterMetric is not { } metric || !double.IsFinite(metric) || metric <= 0)
            return false;

        if (autoTune.JitterAutoSwitchThreshold <= 0)
            return false;

        if (metric <= autoTune.JitterAutoSwitchThreshold)
            return false;

        if (o.OutlierDetector is not null)
            return false;

        return o.OutlierMode == OutlierMode.IqrFence;
    }

    private static IReadOnlyList<string> BuildJitterSwitchWarning(double? jitterMetric)
    {
        var metricLabel = jitterMetric.HasValue
            ? $"{jitterMetric.Value:F2}"
            : "unknown";

        return
        [
            $"Pre-flight jitter probe measured a jitter metric (MAD/median) of {metricLabel} "
            + "on the busy-weight loop, exceeding the auto-switch threshold. The outlier detector "
            + "has been switched from IQR fence to Median Absolute Deviation for this run - MAD's "
            + "higher breakdown point is more resilient to the heavy-tailed samples a noisy host "
            + "produces. Investigate the host (shared-tenant CI runner, thermal throttling, "
            + "frequency scaling) before trusting these numbers. Set AutoTune.JitterAutoSwitchThreshold "
            + "to 0 to disable the auto-switch while keeping the probe, or "
            + "AutoTune.EnableJitterCalibration to false to skip the probe entirely.",
        ];
    }

    private static IReadOnlyList<string> BuildStopWarnings(
        WarmupStopReason warmupStop,
        SampleStopReason sampleStop,
        bool calibrationCapped,
        TimeSpan maxTuningTime,
        double achievedCi,
        double ciTarget,
        int maxSamples,
        int sampleCount,
        int? explicitSamples,
        double capGraceFactor,
        double warmupBudgetFraction,
        bool warmupTimeFloorMet,
        int measurementRestarts,
        double splitHalfDrift,
        double coefficientOfVariation,
        bool hasIterationHooks,
        int resolvedWarmup)
    {
        var capLabel = BenchmarkFormatter.FormatDuration(maxTuningTime);

        // Calibration and warmup stop at their shared share of the cap, not the full cap, so the
        // message names the share (e.g. "40% of the 20 s tuning cap") rather than the full value -
        // otherwise a warmup that stopped after 8 s reads as "stopped at the 20 s cap".
        var sharePct = $"{warmupBudgetFraction * 100:0.#}%";

        if (calibrationCapped)
        {
            return
            [
                $"Calibration exhausted its calibration+warmup budget ({sharePct} of the {capLabel} tuning cap) "
                + "before ops-per-sample could be resolved. The chosen K may be suboptimal. "
                + "Consider increasing --max-tuning-time or --warmup-budget-fraction.",
            ];
        }

        if (sampleStop == SampleStopReason.WallClockCap)
        {
            // When the user pinned --iterations, the cap prevented the loop from collecting the
            // requested count. The auto-mode text ("pinning --iterations") would be misleading
            // because iterations were already pinned; say how many of the requested samples were
            // collected and point at --max-tuning-time or a lower pinned count instead.
            if (explicitSamples is { } pinned and > 0)
            {
                return
                [
                    $"Measurement stopped at the wall-clock tuning cap ({capLabel}) "
                    + $"after collecting {sampleCount} of the pinned {pinned} iterations. "
                    + "The reported statistics are based on fewer samples than requested. "
                    + "Consider increasing --max-tuning-time or reducing --iterations.",
                ];
            }

            return
            [
                $"Measurement stopped at the wall-clock tuning cap ({capLabel}) "
                + "before reaching the confidence-interval target. The reported error margin may be wider than requested. "
                + "Consider increasing --max-tuning-time or pinning --iterations.",
            ];
        }

        if (sampleStop == SampleStopReason.DriftUnresolved)
        {
            return
            [
                $"The measured samples were still drifting after {measurementRestarts} restart(s): the first and "
                + $"second halves of the stream differ by {splitHalfDrift * 100:F1}%, well beyond the "
                + "drift tolerance. The reported confidence interval describes a moving target rather than a "
                + "stable measurement, so the centre is not reproducible however narrow the interval looks. "
                + "The usual cause is a JIT tier-up or dynamic-PGO re-optimization landing inside measurement - "
                + "raise --min-warmup-time so it lands during warmup instead. Otherwise the body itself is "
                + "non-stationary (thermal ramp, a filling cache, a growing data structure); check host "
                + "thermal/load state, or use --launch-count to measure across-launch spread.",
            ];
        }

        if (sampleStop == SampleStopReason.GraceCapExhausted)
        {
            return
            [
                $"Measurement stopped at the grace ceiling ({capLabel} * {capGraceFactor:F1}) "
                + $"after collecting only {sampleCount} samples, below the minimum required for a "
                + "reliable confidence interval. The reported error margin is unreliable. "
                + "Consider increasing --max-tuning-time, reducing --min-samples, or pinning --iterations.",
            ];
        }

        if (sampleStop == SampleStopReason.MaxCeiling && achievedCi > ciTarget)
        {
            // Name the CV: the required sample count grows as (t * CV / target)^2, so for a genuinely
            // noisy body the ceiling is not the problem and raising it will not help. Spelling out the
            // count it would actually take is what makes that concrete.
            var cvClause = double.IsFinite(coefficientOfVariation) && coefficientOfVariation > 0
                ? $" The raw coefficient of variation is {coefficientOfVariation * 100:F0}%, so this rule would need "
                  + $"roughly {EstimateSamplesForTarget(coefficientOfVariation, ciTarget):N0} samples to converge - "
                  + "at that spread the body's variance is the finding, not the sample count."
                : string.Empty;

            return
            [
                $"Measurement stopped at the sample ceiling ({maxSamples:N0}) "
                + $"before reaching the confidence-interval target (achieved ±{achievedCi * 100:F1}% vs target ±{ciTarget * 100:F1}%). "
                + "The reported error margin is wider than requested."
                + cvClause
                + " Consider loosening --ci-target if the body is genuinely noisy, raising --max-samples, "
                + "or pinning --iterations for a deterministic count. For short bodies (<100 ns), the variance is often "
                + "dominated by timer overhead and scheduler jitter rather than the code under test - use --launch-count "
                + "to measure across-launch spread, which is usually the more honest signal.",
            ];
        }

        if (warmupStop == WarmupStopReason.WallClockCap)
        {
            var floorClause = warmupTimeFloorMet
                ? "The warmup time floor was reached, so tiered compilation has most likely landed, but the body "
                  + "was still getting faster when warmup stopped."
                : "The warmup time floor was NOT reached, so the body may still be running pre-tier-1 "
                  + "(unoptimized) code and the measured number can be several times slower than steady state "
                  + "while still showing a tight error margin.";

            return
            [
                $"Warmup exhausted its calibration+warmup budget ({sharePct} of the {capLabel} tuning cap) "
                + $"before the body reached a steady state. {floorClause} "
                + "Consider increasing --max-tuning-time, raising --warmup-budget-fraction, or pinning --warmup.",
            ];
        }

        // Warmup hitting its sample ceiling used to exit silently. That is the most dangerous quiet
        // failure in the loop: a fast body needs tens of thousands of samples to reach the time floor,
        // so a ceiling that binds first means measurement begins on cold code with nothing said about it.
        if (warmupStop == WarmupStopReason.MaxCeiling)
        {
            // Iteration setup/teardown runs outside the timed window, so accumulated in-body time
            // materially under-counts real elapsed time and the floor check is not meaningful there.
            if (!warmupTimeFloorMet && !hasIterationHooks)
            {
                return
                [
                    $"Warmup stopped at the sample ceiling ({resolvedWarmup:N0} iterations) without reaching the "
                    + "warmup time floor, so the body may still be running pre-tier-1 (unoptimized) code. "
                    + "A measurement taken there can be several times slower than steady state while still "
                    + "reporting a tight error margin, and it will not reproduce across runs. "
                    + "Consider raising --max-warmup, lowering --min-warmup-time, or pinning --warmup. "
                    + "A body too fast to accumulate the floor within the ceiling (typically with "
                    + "--ops-per-sample pinned to 1) needs a larger --ops-per-sample so each sample spans more work.",
                ];
            }

            return
            [
                $"Warmup stopped at the sample ceiling ({resolvedWarmup:N0} iterations) without ever reaching a "
                + "plateau - the body was still getting faster. The measured samples may be affected by "
                + "ongoing JIT or cache warm-up. Consider raising --max-warmup or pinning --warmup.",
            ];
        }

        return [];
    }

    /// <summary>
    ///     The sample count the CI-width rule would need for a given coefficient of variation and
    ///     target half-width, from <c>n ~= (t * CV / target)^2</c> with <c>t ~= 1.96</c>. Used only to
    ///     make a ceiling-stop warning concrete, so the normal approximation is ample.
    /// </summary>
    private static double EstimateSamplesForTarget(double coefficientOfVariation, double ciTarget)
    {
        if (ciTarget <= 0)
            return double.PositiveInfinity;

        var n = Math.Pow(1.96 * coefficientOfVariation / ciTarget, 2);
        return double.IsFinite(n) ? Math.Ceiling(n) : double.PositiveInfinity;
    }

    private static (double elapsedNs, long allocDelta, DiagnosticDelta diagDelta) AcquireSampleSync(
        Action body, RunSpec spec, IClock clock, int k, bool measureAllocations, bool forceGc, DiagnosticsOptions diagnostics)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();

        AllocationMeter.AllocationSnapshot snapshot = default;

        if (measureAllocations)
            snapshot = AllocationMeter.Capture();

        var diagSnapshot = diagnostics.Any ? DiagnosticMeter.Capture(diagnostics) : default;

        var timestamp = clock.GetTimestamp();

        for (var j = 0; j < k; j++)
        {
            body();
        }

        var elapsedNs = clock.GetElapsedNanoseconds(timestamp);

        var allocDelta = measureAllocations ? AllocationMeter.Delta(snapshot) : 0L;
        var diagDelta = diagnostics.Any ? DiagnosticMeter.Delta(diagSnapshot, diagnostics) : default;

        spec.IterationTeardown?.Invoke();
        return (elapsedNs, allocDelta, diagDelta);
    }

    private static async Task<(double elapsedNs, long allocDelta, DiagnosticDelta diagDelta)> AcquireSampleAsync(
        Func<Task> body, RunSpec spec, IClock clock, int k, bool measureAllocations, bool forceGc, DiagnosticsOptions diagnostics)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();

        AllocationMeter.AllocationSnapshot snapshot = default;

        if (measureAllocations)
            snapshot = AllocationMeter.Capture();

        var diagSnapshot = diagnostics.Any ? DiagnosticMeter.Capture(diagnostics) : default;

        var timestamp = clock.GetTimestamp();

        for (var j = 0; j < k; j++)
        {
            await body().ConfigureAwait(false);
        }

        var elapsedNs = clock.GetElapsedNanoseconds(timestamp);

        var allocDelta = measureAllocations ? AllocationMeter.Delta(snapshot) : 0L;
        var diagDelta = diagnostics.Any ? DiagnosticMeter.Delta(diagSnapshot, diagnostics) : default;

        spec.IterationTeardown?.Invoke();
        return (elapsedNs, allocDelta, diagDelta);
    }

    private static void RunUntimedSampleSync(Action body, RunSpec spec, int k, bool forceGc)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();

        for (var j = 0; j < k; j++)
        {
            body();
        }

        spec.IterationTeardown?.Invoke();
    }

    private static async Task RunUntimedSampleAsync(Func<Task> body, RunSpec spec, int k, bool forceGc)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();

        for (var j = 0; j < k; j++)
        {
            await body().ConfigureAwait(false);
        }

        spec.IterationTeardown?.Invoke();
    }

    private static int ProgressCadence(int total)
    {
        var interval = Math.Max(1, total / 20);
        return Math.Min(interval, 50);
    }
}

/// <summary>The output of one <see cref="AdaptiveLoop" /> pass: per-op timings/allocations, the resolved diagnostic, and any loop-level warnings.</summary>
internal readonly record struct AdaptiveResult(
    double[] PerOpTimings,
    long[]? PerOpAllocations,
    DiagnosticDelta[]? PerOpDiagnostics,
    long? ExceptionCount,
    HeapSnapshot? HeapInfo,
    TimeSpan MeasuredDuration,
    int ResolvedWarmup,
    AutoTuneDiagnostic Diagnostic,
    IReadOnlyList<string> Warnings,
    IOutlierDetector? EffectiveOutlierDetector = null);
