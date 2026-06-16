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

    public static AdaptiveResult Run(
        string name,
        Action body,
        RunSpec spec,
        IClock clock,
        IBenchmarkProgress progress,
        CancellationToken ct)
    {
        var o = spec.Options;
        var autoTune = o.AutoTune;
        var measureAllocations = o.MeasureAllocations;
        var forceGc = o.ForceGcBeforeEachIteration;
        var maxTuningNs = autoTune.MaxTuningTime.TotalNanoseconds;
        var calibrate = IsEligibleForCalibration(o, spec);
        var k = o.OpsPerSample ?? 1;
        double accumulatedNs = 0;
        long totalBodyInvocations = 0;

        // Start the tuning-wall-clock span here, after the runner's pre-loop progress callback, so
        // the reported time covers only the adaptive loop's own work.
        var tuningStartTimestamp = clock.GetTimestamp();

        // ----- Phase A: ops-per-sample calibration -----
        var calibrationCapped = false;

        if (calibrate)
        {
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
                    var (elapsed, _) = AcquireSampleSync(body, spec, clock, probeK, false, forceGc);
                    totalBodyInvocations += probeK;
                    accumulatedNs += elapsed;
                    if (elapsed < best)
                        best = elapsed;
                }

                var resolved = calibrator.Feed(best);
                k = calibrator.OpsPerSample;

                if (resolved)
                    break;

                if (accumulatedNs >= maxTuningNs)
                {
                    calibrationCapped = true;
                    break;
                }
            }
        }

        // ----- Phase B: warmup -----
        int resolvedWarmup;
        WarmupStopReason warmupStop;

        if (calibrationCapped)
        {
            resolvedWarmup = 0;
            warmupStop = WarmupStopReason.WallClockCap;
        }
        else if (o.WarmupIterations is { } explicitWarmup)
        {
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
            var detector = new WarmupPlateauDetector(autoTune);
            warmupStop = WarmupStopReason.Settled;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (elapsed, _) = AcquireSampleSync(body, spec, clock, k, false, forceGc);
                totalBodyInvocations += k;
                accumulatedNs += elapsed;

                if (detector.Feed(elapsed / k))
                {
                    warmupStop = detector.StopReason;
                    break;
                }

                if (accumulatedNs >= maxTuningNs)
                {
                    warmupStop = WarmupStopReason.WallClockCap;
                    break;
                }
            }

            resolvedWarmup = detector.Count;
        }

        progress.OnWarmupCompleted(name).GetAwaiter().GetResult();

        // Pre-measurement full GC is intentionally profile-gated (and has been since the
        // MeasurementProfile feature, not a change introduced by the adaptive loop): the Independent
        // profile clears warmup garbage so it cannot trigger a collection mid-measurement, while the
        // default Realistic profile deliberately inherits the warmup heap to match production.
        if (o.ForceGcBetweenBenchmarks)
            GcControl.ForceFullGc();

        // ----- Phase C: measurement -----
        var explicitSamples = o.Iterations;
        var timings = new List<double>(explicitSamples ?? autoTune.MinSamples);
        var allocations = measureAllocations ? new List<long>(timings.Capacity) : null;
        var ci = explicitSamples is null ? new CiWidthDetector(o.ConfidenceLevel, autoTune) : null;

        // In auto mode the sample count is resolved at runtime, so there is no honest total to
        // report - signal indeterminate (0) to the progress UI while still bounding how often we
        // notify by the ceiling.
        var reportedTotal = explicitSamples ?? 0;
        var progressInterval = ProgressCadence(explicitSamples ?? autoTune.MaxSamples);

        var measureStartTimestamp = clock.GetTimestamp();
        var sampleCount = 0;
        SampleStopReason sampleStop;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (elapsed, allocDelta) = AcquireSampleSync(body, spec, clock, k, measureAllocations, forceGc);
            totalBodyInvocations += k;
            accumulatedNs += elapsed;
            var perOp = elapsed / k;
            timings.Add(perOp);
            allocations?.Add(allocDelta / k);
            sampleCount++;

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
            else if (ci.Feed(perOp))
            {
                sampleStop = ci.StopReason;
                break;
            }

            if (accumulatedNs >= maxTuningNs)
            {
                sampleStop = SampleStopReason.WallClockCap;
                break;
            }
        }

        var measuredDuration = clock.GetElapsedTime(measureStartTimestamp);

        return BuildResult(
            timings, allocations, measuredDuration, resolvedWarmup, sampleCount, k,
            totalBodyInvocations, warmupStop, sampleStop, o.ConfidenceLevel,
            clock.GetElapsedTime(tuningStartTimestamp));
    }

    public static async Task<AdaptiveResult> RunAsync(
        string name,
        Func<Task> body,
        RunSpec spec,
        IClock clock,
        IBenchmarkProgress progress,
        CancellationToken ct)
    {
        var o = spec.Options;
        var autoTune = o.AutoTune;
        var measureAllocations = o.MeasureAllocations;
        var forceGc = o.ForceGcBeforeEachIteration;
        var maxTuningNs = autoTune.MaxTuningTime.TotalNanoseconds;
        var calibrate = IsEligibleForCalibration(o, spec);
        var k = o.OpsPerSample ?? 1;
        double accumulatedNs = 0;
        long totalBodyInvocations = 0;

        // Start the tuning-wall-clock span here, after the runner's pre-loop progress callback, so
        // the reported time covers only the adaptive loop's own work.
        var tuningStartTimestamp = clock.GetTimestamp();

        // ----- Phase A: ops-per-sample calibration -----
        var calibrationCapped = false;

        if (calibrate)
        {
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
                    var (elapsed, _) = await AcquireSampleAsync(body, spec, clock, probeK, false, forceGc)
                        .ConfigureAwait(false);
                    totalBodyInvocations += probeK;
                    accumulatedNs += elapsed;
                    if (elapsed < best)
                        best = elapsed;
                }

                var resolved = calibrator.Feed(best);
                k = calibrator.OpsPerSample;

                if (resolved)
                    break;

                if (accumulatedNs >= maxTuningNs)
                {
                    calibrationCapped = true;
                    break;
                }
            }
        }

        // ----- Phase B: warmup -----
        int resolvedWarmup;
        WarmupStopReason warmupStop;

        if (calibrationCapped)
        {
            resolvedWarmup = 0;
            warmupStop = WarmupStopReason.WallClockCap;
        }
        else if (o.WarmupIterations is { } explicitWarmup)
        {
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
            var detector = new WarmupPlateauDetector(autoTune);
            warmupStop = WarmupStopReason.Settled;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (elapsed, _) = await AcquireSampleAsync(body, spec, clock, k, false, forceGc).ConfigureAwait(false);
                totalBodyInvocations += k;
                accumulatedNs += elapsed;

                if (detector.Feed(elapsed / k))
                {
                    warmupStop = detector.StopReason;
                    break;
                }

                if (accumulatedNs >= maxTuningNs)
                {
                    warmupStop = WarmupStopReason.WallClockCap;
                    break;
                }
            }

            resolvedWarmup = detector.Count;
        }

        await progress.OnWarmupCompleted(name).ConfigureAwait(false);

        // Pre-measurement full GC is intentionally profile-gated (and has been since the
        // MeasurementProfile feature, not a change introduced by the adaptive loop): the Independent
        // profile clears warmup garbage so it cannot trigger a collection mid-measurement, while the
        // default Realistic profile deliberately inherits the warmup heap to match production.
        if (o.ForceGcBetweenBenchmarks)
            GcControl.ForceFullGc();

        // ----- Phase C: measurement -----
        var explicitSamples = o.Iterations;
        var timings = new List<double>(explicitSamples ?? autoTune.MinSamples);
        var allocations = measureAllocations ? new List<long>(timings.Capacity) : null;
        var ci = explicitSamples is null ? new CiWidthDetector(o.ConfidenceLevel, autoTune) : null;

        // In auto mode the sample count is resolved at runtime, so there is no honest total to
        // report - signal indeterminate (0) to the progress UI while still bounding how often we
        // notify by the ceiling.
        var reportedTotal = explicitSamples ?? 0;
        var progressInterval = ProgressCadence(explicitSamples ?? autoTune.MaxSamples);

        var measureStartTimestamp = clock.GetTimestamp();
        var sampleCount = 0;
        SampleStopReason sampleStop;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (elapsed, allocDelta) = await AcquireSampleAsync(body, spec, clock, k, measureAllocations, forceGc)
                .ConfigureAwait(false);
            totalBodyInvocations += k;
            accumulatedNs += elapsed;
            var perOp = elapsed / k;
            timings.Add(perOp);
            allocations?.Add(allocDelta / k);
            sampleCount++;

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
            else if (ci.Feed(perOp))
            {
                sampleStop = ci.StopReason;
                break;
            }

            if (accumulatedNs >= maxTuningNs)
            {
                sampleStop = SampleStopReason.WallClockCap;
                break;
            }
        }

        var measuredDuration = clock.GetElapsedTime(measureStartTimestamp);

        return BuildResult(
            timings, allocations, measuredDuration, resolvedWarmup, sampleCount, k,
            totalBodyInvocations, warmupStop, sampleStop, o.ConfidenceLevel,
            clock.GetElapsedTime(tuningStartTimestamp));
    }

    private static bool IsEligibleForCalibration(MeasurementOptions o, RunSpec spec)
        => o.OpsPerSample is null
           && spec.IterationSetup is null
           && spec.IterationTeardown is null
           && !o.ForceGcBeforeEachIteration;

    private static AdaptiveResult BuildResult(
        List<double> timings,
        List<long>? allocations,
        TimeSpan measuredDuration,
        int resolvedWarmup,
        int sampleCount,
        int opsPerSample,
        long totalBodyInvocations,
        WarmupStopReason warmupStop,
        SampleStopReason sampleStop,
        double confidenceLevel,
        TimeSpan tuningWallClock)
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
            TotalBodyInvocations = totalBodyInvocations,
            WarmupStop = warmupStop,
            SampleStop = sampleStop,
            AchievedRelativeCiWidth = achievedCi,
            TuningWallClock = tuningWallClock,
        };

        return new AdaptiveResult(timingsArray, allocations?.ToArray(), measuredDuration, resolvedWarmup, diagnostic);
    }

    private static (double elapsedNs, long allocDelta) AcquireSampleSync(
        Action body, RunSpec spec, IClock clock, int k, bool measureAllocations, bool forceGc)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();

        AllocationMeter.AllocationSnapshot snapshot = default;
        if (measureAllocations)
            snapshot = AllocationMeter.Capture();

        var timestamp = clock.GetTimestamp();
        for (var j = 0; j < k; j++)
            body();
        var elapsedNs = clock.GetElapsedNanoseconds(timestamp);

        var allocDelta = measureAllocations ? AllocationMeter.Delta(snapshot) : 0L;

        spec.IterationTeardown?.Invoke();
        return (elapsedNs, allocDelta);
    }

    private static async Task<(double elapsedNs, long allocDelta)> AcquireSampleAsync(
        Func<Task> body, RunSpec spec, IClock clock, int k, bool measureAllocations, bool forceGc)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();

        AllocationMeter.AllocationSnapshot snapshot = default;
        if (measureAllocations)
            snapshot = AllocationMeter.Capture();

        var timestamp = clock.GetTimestamp();
        for (var j = 0; j < k; j++)
            await body().ConfigureAwait(false);
        var elapsedNs = clock.GetElapsedNanoseconds(timestamp);

        var allocDelta = measureAllocations ? AllocationMeter.Delta(snapshot) : 0L;

        spec.IterationTeardown?.Invoke();
        return (elapsedNs, allocDelta);
    }

    private static void RunUntimedSampleSync(Action body, RunSpec spec, int k, bool forceGc)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();
        for (var j = 0; j < k; j++)
            body();
        spec.IterationTeardown?.Invoke();
    }

    private static async Task RunUntimedSampleAsync(Func<Task> body, RunSpec spec, int k, bool forceGc)
    {
        if (forceGc)
            GcControl.ForceGen0Collection();

        spec.IterationSetup?.Invoke();
        for (var j = 0; j < k; j++)
            await body().ConfigureAwait(false);
        spec.IterationTeardown?.Invoke();
    }

    private static int ProgressCadence(int total)
    {
        var interval = Math.Max(1, total / 20);
        return Math.Min(interval, 50);
    }
}

/// <summary>The output of one <see cref="AdaptiveLoop" /> pass: per-op timings/allocations and the resolved diagnostic.</summary>
internal readonly record struct AdaptiveResult(
    double[] PerOpTimings,
    long[]? PerOpAllocations,
    TimeSpan MeasuredDuration,
    int ResolvedWarmup,
    AutoTuneDiagnostic Diagnostic);
