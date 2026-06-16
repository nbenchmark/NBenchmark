namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Decides when a body has reached steady state using a one-sided plateau rule. Warmup is
///     one-directional - a body only gets faster as the JIT and caches warm - so the detector
///     watches the mean of each batch of samples and ends warmup once it has stopped getting
///     meaningfully faster for a run of consecutive batches.
/// </summary>
internal sealed class WarmupPlateauDetector
{
    private readonly int _minWarmup;
    private readonly int _maxWarmup;
    private readonly double _epsilon;
    private readonly int _patience;
    private readonly int _batchSize;

    private double _batchSum;
    private int _batchCount;
    private double _best = double.PositiveInfinity;
    private int _nonImproving;
    private int _total;

    public WarmupPlateauDetector(AutoTuneOptions options)
    {
        _minWarmup = Math.Max(0, options.MinWarmup);
        _maxWarmup = Math.Max(_minWarmup, options.MaxWarmup);
        _epsilon = options.WarmupEpsilon;
        _patience = Math.Max(1, options.PlateauPatience);
        _batchSize = Math.Max(1, options.BatchSize);
    }

    /// <summary>Whether warmup has settled or hit its ceiling.</summary>
    public bool Resolved { get; private set; }

    /// <summary>Why warmup stopped (<see cref="WarmupStopReason.Settled" /> or <see cref="WarmupStopReason.MaxCeiling" />).</summary>
    public WarmupStopReason StopReason { get; private set; }

    /// <summary>The number of warmup samples fed so far.</summary>
    public int Count => _total;

    /// <summary>
    ///     Reports the per-op nanoseconds of one warmup sample. Returns <c>true</c> when warmup is
    ///     resolved (the caller should move to measurement).
    /// </summary>
    public bool Feed(double perOpNs)
    {
        if (Resolved)
            return true;

        _total++;
        _batchSum += perOpNs;
        _batchCount++;

        if (_total >= _maxWarmup)
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

        // "Improving" means at least WarmupEpsilon faster than the best batch seen so far.
        if (batchMean < _best * (1.0 - _epsilon))
            _nonImproving = 0;
        else
            _nonImproving++;

        if (batchMean < _best)
            _best = batchMean;

        if (_total >= _minWarmup && _nonImproving >= _patience)
        {
            Resolved = true;
            StopReason = WarmupStopReason.Settled;
            return true;
        }

        return false;
    }
}
