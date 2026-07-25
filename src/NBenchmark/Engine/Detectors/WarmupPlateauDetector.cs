namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Decides when a body has reached steady state using a one-sided plateau rule. Warmup is
///     one-directional - a body only gets faster as the JIT and caches warm - so the detector
///     watches the mean of each batch of samples and ends warmup once it has stopped getting
///     meaningfully faster for a run of consecutive batches.
/// </summary>
internal sealed class WarmupPlateauDetector
{
    private readonly int _batchSize;
    private readonly double _epsilon;
    private readonly double _jitGateDeactivateNs;
    private readonly double _jitQuietPeriodNs;
    private readonly int _maxWarmup;
    private readonly int _minWarmup;
    private readonly double _minWarmupTimeNs;
    private readonly int _patience;
    private readonly bool _requireJitQuiescence;
    private readonly WarmupCurveRecorder _curve;
    private int _batchCount;

    private double _batchSum;
    private double _best = double.PositiveInfinity;
    private long _jitBaseline = -1;
    private long _lastJitCount = -1;
    private double _lastJitChangeAtNs;
    private int _nonImproving;
    private double _warmupElapsedNs;

    public WarmupPlateauDetector(AutoTuneOptions options)
        : this(options, perSampleEstimateNs: 0.0)
    {
    }

    /// <summary>
    ///     Constructs the detector with an optional per-sample elapsed estimate (in nanoseconds)
    ///     from ops-per-sample calibration. When the estimate is positive and the body is slow
    ///     enough that the configured <see cref="AutoTuneOptions.BatchSize" /> would span well over
    ///     the warmup batch target (250 ms), the effective batch is shrunk so the plateau rule can
    ///     settle in a reasonable number of samples instead of spending the whole warmup budget on
    ///     a handful of full-length batches. A 2 s body with default BatchSize = 8 would otherwise
    ///     have to feed (Patience + 1) * BatchSize = 32 samples (64 s) before the plateau rule can
    ///     settle; shrinking the batch to 1 drops the plateau requirement to (Patience + 1) = 4
    ///     samples, after which <see cref="AutoTuneOptions.MinWarmup" /> (default 8) becomes the
    ///     binding floor. The estimate is 0 when calibration did not run (K pinned, setup/teardown,
    ///     forced GC), in which case the configured BatchSize is used unchanged.
    ///     <para>
    ///         Batch shrinking only lowers the sample <em>count</em> needed to settle; the
    ///         calibration+warmup wall-clock share (<see cref="AutoTuneOptions.WarmupBudgetFraction" />
    ///         of <see cref="AutoTuneOptions.MaxTuningTime" />) is the independent time bound and
    ///         typically stops a genuinely slow body's warmup before either floor is reached.
    ///     </para>
    /// </summary>
    public WarmupPlateauDetector(AutoTuneOptions options, double perSampleEstimateNs)
    {
        _minWarmup = Math.Max(0, options.MinWarmup);
        _maxWarmup = Math.Max(_minWarmup, options.MaxWarmup);
        _epsilon = options.WarmupEpsilon;
        _patience = Math.Max(1, options.PlateauPatience);
        _batchSize = ResolveBatchSize(options.BatchSize, perSampleEstimateNs);
        _minWarmupTimeNs = Math.Max(0, options.MinWarmupTime.Ticks * 100.0);
        _requireJitQuiescence = options.RequireJitQuiescence;

        // Clamp the quiet period down to the time floor so the gate can never become the binding
        // floor itself. This keeps the composition predictable and documentable: when nothing is
        // compiling, warmup ends at MinWarmupTime; when something is, warmup extends until the quiet
        // period elapses, bounded by the deactivation threshold below. Without the clamp, setting
        // MinWarmupTime = 10 ms would silently yield a 50 ms floor from the default quiet period.
        _jitQuietPeriodNs = Math.Min(Math.Max(0, options.JitQuietPeriod.Ticks * 100.0), _minWarmupTimeNs);

        // The JIT-quiescence gate stops blocking once warmup has run 4 x the time floor, so a busy
        // host that JITs unrelated code cannot hold warmup open forever. A zero floor leaves this at
        // zero, which disables the gate (WarmupGates treats a zero floor as off).
        _jitGateDeactivateNs = _minWarmupTimeNs * 4.0;

        _curve = new WarmupCurveRecorder(_batchSize);
    }

    private static int ResolveBatchSize(int configured, double perSampleEstimateNs)
    {
        if (perSampleEstimateNs <= 0)
            return Math.Max(1, configured);

        // Scale the batch so one batch spans roughly the 250 ms target. A slow body (2 s/sample)
        // resolves to a batch of 1; a fast body (1 µs/sample) resolves to 250_000, clamped to the
        // configured BatchSize. The floor is 1 (one sample per batch) and the ceiling is the
        // configured BatchSize - slow bodies shrink the batch, fast bodies are unaffected.
        const double targetBatchNs = 250_000_000.0;
        var scaled = (int)Math.Ceiling(targetBatchNs / perSampleEstimateNs);

        return Math.Clamp(scaled, 1, Math.Max(1, configured));
    }

    /// <summary>Whether warmup has settled or hit its ceiling.</summary>
    public bool Resolved { get; private set; }

    /// <summary>Why warmup stopped (<see cref="WarmupStopReason.Settled" /> or <see cref="WarmupStopReason.MaxCeiling" />).</summary>
    public WarmupStopReason StopReason { get; private set; }

    /// <summary>The number of warmup samples fed so far.</summary>
    public int Count { get; private set; }

    /// <summary>
    ///     The number of samples grouped into one warmup batch, after the slow-body shrink. The caller
    ///     uses this to read the JIT compiled-method count only at batch boundaries, since that is the
    ///     only point the gate consults it.
    /// </summary>
    public int EffectiveBatchSize => _batchSize;

    /// <summary>
    ///     Whether accumulated warmup reached <see cref="AutoTuneOptions.MinWarmupTime" />. Readable
    ///     after every exit path - settled, <see cref="WarmupStopReason.MaxCeiling" />, or the caller's
    ///     budget cap - so the caller can warn when warmup was cut short of the floor and the body may
    ///     still be running pre-tier-1 code.
    /// </summary>
    public bool TimeFloorMet => _warmupElapsedNs >= _minWarmupTimeNs;

    /// <summary>
    ///     How many methods the JIT compiled over the course of warmup (the count at the most recent
    ///     batch boundary minus the baseline captured at the first), or <c>0</c> when no boundary has
    ///     been reached. Reported as a diagnostic: a large value alongside a short warmup is the
    ///     signature of a body measured mid-tier-up.
    /// </summary>
    public long JitCompiledDelta => _jitBaseline >= 0 && _lastJitCount >= 0
        ? _lastJitCount - _jitBaseline
        : 0;

    /// <summary>
    ///     The mean per-op nanoseconds of the most recently completed batch, or <c>0</c> before the
    ///     first batch completes. This is the warm steady-state estimate the caller feeds into
    ///     post-warmup ops-per-sample recalibration (<see cref="WarmupRecalibration" />).
    /// </summary>
    public double LastBatchMeanPerOp { get; private set; }

    /// <summary>
    ///     How far into warmup, in nanoseconds, the JIT compiled-method count last moved - or <c>0</c>
    ///     when it never did. This is the closest thing the engine has to a tier-up landing marker:
    ///     with the body under continuous load, the last compilation is typically the promotion of the
    ///     hot path itself. Compare against <see cref="WarmupElapsedNs" /> to see how much quiet time
    ///     followed it.
    /// </summary>
    public double JitLastChangeAtNs => _lastJitChangeAtNs;

    /// <summary>Total warmup elapsed nanoseconds, summed across every warmup sample.</summary>
    public double WarmupElapsedNs => _warmupElapsedNs;

    /// <summary>
    ///     Whether warmup ended with the JIT genuinely quiet - the configured quiet period elapsed with
    ///     no compilation - as opposed to the gate having been bypassed by its deactivation threshold or
    ///     never having been required. When this is <c>false</c> and the gate was required, measurement
    ///     may have started while compilation was still in flight.
    /// </summary>
    public bool JitQuiescenceAchieved => !_requireJitQuiescence
        || _minWarmupTimeNs <= 0
        || _warmupElapsedNs - _lastJitChangeAtNs >= _jitQuietPeriodNs;

    /// <summary>
    ///     The warmup curve: one mean per-op reading per warmup batch, oldest first, decimated to a
    ///     bounded length. Empty when no batch completed. See <see cref="WarmupCurveRecorder" />.
    /// </summary>
    public double[] Curve => _curve.ToArray();

    /// <summary>Warmup samples between consecutive <see cref="Curve" /> points.</summary>
    public int CurveSampleInterval => _curve.SampleInterval;

    /// <summary>
    ///     Reports one warmup sample: its per-op nanoseconds (for the plateau rule), its raw elapsed
    ///     nanoseconds (for the <see cref="AutoTuneOptions.MinWarmupTime" /> floor), and the process
    ///     JIT compiled-method count read just after the sample (for the JIT-quiescence gate).
    ///     Returns <c>true</c> when warmup is resolved (the caller should move to measurement).
    /// </summary>
    /// <param name="perOpNs">The sample's per-op nanoseconds.</param>
    /// <param name="elapsedNs">The sample's raw elapsed nanoseconds (per-op times ops-per-sample).</param>
    /// <param name="jitCompiledMethodCount">
    ///     The process JIT compiled-method count read just after the sample, or <c>-1</c> for "not
    ///     sampled". The gate only consults this at batch boundaries, so the caller may skip the read
    ///     on non-boundary samples and pass <c>-1</c> - which matters once warmup spans tens of
    ///     thousands of samples, and because the read itself can allocate and trigger JIT activity,
    ///     perturbing the very signal it reports.
    /// </param>
    public bool Feed(double perOpNs, double elapsedNs, long jitCompiledMethodCount)
    {
        if (Resolved)
            return true;

        Count++;
        _batchSum += perOpNs;
        _batchCount++;
        _warmupElapsedNs += elapsedNs;

        if (Count >= _maxWarmup)
        {
            Resolved = true;
            StopReason = WarmupStopReason.MaxCeiling;
            return true;
        }

        if (_batchCount < _batchSize)
            return false;

        var batchMean = _batchSum / _batchCount;
        _batchSum = 0;
        _batchCount = 0;
        LastBatchMeanPerOp = batchMean;

        // Retain the batch mean as one point on the warmup curve. This is the only record of the
        // tier-0 → tier-1 decay that survives the run: raw warmup timings are never persisted.
        _curve.Add(batchMean);

        // Track *where in warmup* the compiled-method count last moved, rather than whether it moved
        // during this one batch - the gate needs a sustained quiet interval, see WarmupGates.CanSettle.
        // A negative count means the caller did not sample it here, so leave the state alone.
        if (jitCompiledMethodCount >= 0)
        {
            // Capture the baseline at the first sampled boundary so JitCompiledDelta is well-defined.
            if (_jitBaseline < 0)
                _jitBaseline = jitCompiledMethodCount;
            else if (jitCompiledMethodCount != _lastJitCount)
                _lastJitChangeAtNs = _warmupElapsedNs;

            _lastJitCount = jitCompiledMethodCount;
        }

        // "Improving" means at least WarmupEpsilon faster than the best batch seen so far.
        if (batchMean < _best * (1.0 - _epsilon))
            _nonImproving = 0;
        else
            _nonImproving++;

        if (batchMean < _best)
            _best = batchMean;

        // The plateau rule says the body has stopped getting faster; the settle gates then decide
        // whether it is actually safe to stop (enough warmup time accumulated, JIT quiesced).
        var plateauReached = Count >= _minWarmup && _nonImproving >= _patience;

        if (plateauReached
            && WarmupGates.CanSettle(
                _warmupElapsedNs, _minWarmupTimeNs, _lastJitChangeAtNs,
                _requireJitQuiescence, _jitQuietPeriodNs, _jitGateDeactivateNs))
        {
            Resolved = true;
            StopReason = WarmupStopReason.Settled;
            return true;
        }

        return false;
    }
}
