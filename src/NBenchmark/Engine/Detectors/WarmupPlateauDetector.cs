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
    private readonly int _maxWarmup;
    private readonly int _minWarmup;
    private readonly double _minWarmupTimeNs;
    private readonly int _patience;
    private readonly bool _requireJitQuiescence;
    private int _batchCount;

    private double _batchSum;
    private double _best = double.PositiveInfinity;
    private long _lastBatchJitCount;
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

        // The JIT-quiescence gate stops blocking once warmup has run 4 x the time floor, so a busy
        // host that JITs unrelated code cannot hold warmup open forever. A zero floor leaves this at
        // zero, which disables the gate (WarmupGates treats a zero deactivation threshold as off).
        _jitGateDeactivateNs = _minWarmupTimeNs * 4.0;
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
    ///     The mean per-op nanoseconds of the most recently completed batch, or <c>0</c> before the
    ///     first batch completes. This is the warm steady-state estimate the caller feeds into
    ///     post-warmup ops-per-sample recalibration (<see cref="WarmupRecalibration" />).
    /// </summary>
    public double LastBatchMeanPerOp { get; private set; }

    /// <summary>
    ///     Reports one warmup sample: its per-op nanoseconds (for the plateau rule), its raw elapsed
    ///     nanoseconds (for the <see cref="AutoTuneOptions.MinWarmupTime" /> floor), and the process
    ///     JIT compiled-method count read just after the sample (for the JIT-quiescence gate).
    ///     Returns <c>true</c> when warmup is resolved (the caller should move to measurement).
    /// </summary>
    public bool Feed(double perOpNs, double elapsedNs, long jitCompiledMethodCount)
    {
        if (Resolved)
            return true;

        Count++;
        _batchSum += perOpNs;
        _batchCount++;
        _warmupElapsedNs += elapsedNs;

        // Capture the JIT baseline on the first sample so the first batch's delta is well-defined.
        if (Count == 1)
            _lastBatchJitCount = jitCompiledMethodCount;

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

        // Methods the JIT compiled during this batch (a proxy for in-flight tier-1 promotion).
        var jitDelta = jitCompiledMethodCount - _lastBatchJitCount;
        _lastBatchJitCount = jitCompiledMethodCount;

        // "Improving" means at least WarmupEpsilon faster than the best batch seen so far.
        if (batchMean < _best * (1.0 - _epsilon))
            _nonImproving = 0;
        else
            _nonImproving++;

        if (batchMean < _best)
            _best = batchMean;

        // The plateau rule says the body has stopped getting faster; the settle gates then decide
        // whether it is actually safe to stop (enough wall-clock warmup, JIT quiesced).
        var plateauReached = Count >= _minWarmup && _nonImproving >= _patience;

        if (plateauReached
            && WarmupGates.CanSettle(
                _warmupElapsedNs, _minWarmupTimeNs, jitDelta, _requireJitQuiescence, _jitGateDeactivateNs))
        {
            Resolved = true;
            StopReason = WarmupStopReason.Settled;
            return true;
        }

        return false;
    }
}
