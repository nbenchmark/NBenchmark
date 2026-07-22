using NBenchmark.Stats;

namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Decides when enough measured samples have been collected by tracking the relative
///     half-width of the confidence interval on the mean. A Welford accumulator maintains running
///     <c>n</c>, mean, and sum-of-squared-deviations in O(1) per sample, so the stop rule costs
///     nothing on the hot path. The interval is evaluated on a cadence once past the sample floor.
/// </summary>
/// <remarks>
///     The half-width is computed on the raw (untrimmed) stream, which is a conservative signal -
///     raw variance is at least trimmed variance - so the loop may take a few extra samples but
///     never stops too early. The reported interval is computed separately on the trimmed samples.
/// </remarks>
internal sealed class CiWidthDetector
{
    // Pre-sized to avoid the first few backing-array reallocations during short runs. The
    // series grows at evaluation cadence (every BatchSize samples past MinSamples), not per
    // sample, so even a pessimistic upper bound (MaxSamples / BatchSize = 12,500) is ~100 KB.
    private readonly List<double> _halfWidthSeries = new(capacity: 128);

    private readonly int _cadence;
    private readonly double _ciTarget;
    private readonly double _confidenceLevel;
    private readonly int _maxSamples;
    private readonly int _minSamples;
    private double _m2;

    public CiWidthDetector(double confidenceLevel, AutoTuneOptions options)
    {
        _confidenceLevel = confidenceLevel;
        _ciTarget = options.CiTarget;
        _minSamples = Math.Max(1, options.MinSamples);
        _maxSamples = Math.Max(_minSamples, options.MaxSamples);
        _cadence = Math.Max(1, options.BatchSize);
    }

    /// <summary>Whether measurement has met the CI target or hit its ceiling.</summary>
    public bool Resolved { get; private set; }

    /// <summary>Why measurement stopped (<see cref="SampleStopReason.CiTargetMet" /> or <see cref="SampleStopReason.MaxCeiling" />).</summary>
    public SampleStopReason StopReason { get; private set; }

    /// <summary>The relative CI half-width (half-width / mean) at the most recent evaluation.</summary>
    public double AchievedRelativeHalfWidth { get; private set; } = double.PositiveInfinity;

    /// <summary>
    ///     The relative CI half-width at each evaluation point during measurement, in evaluation
    ///     order. Empty when no evaluation has run yet (e.g. the loop never reached
    ///     <see cref="AutoTuneOptions.MinSamples" />). The final entry may differ slightly from
    ///     <see cref="AchievedRelativeHalfWidth" /> recomputed on the full raw sample set after the
    ///     loop stops - the series uses the Welford accumulator's running stats, while the
    ///     post-loop scalar is computed from the complete trimmed-or-untrimmed array.
    /// </summary>
    public IReadOnlyList<double> HalfWidthSeries => _halfWidthSeries;

    /// <summary>The number of measured samples fed so far.</summary>
    public long Count { get; private set; }

    /// <summary>The running mean of the fed samples.</summary>
    public double Mean { get; private set; }

    /// <summary>The running sample standard deviation (Bessel-corrected) of the fed samples.</summary>
    public double StandardDeviation => Count >= 2 ? Math.Sqrt(_m2 / (Count - 1)) : 0.0;

    /// <summary>
    ///     Reports the per-op nanoseconds of one measured sample. Returns <c>true</c> when the
    ///     measurement phase is resolved (the caller should stop collecting samples).
    /// </summary>
    public bool Feed(double perOpNs)
    {
        if (Resolved)
            return true;

        // Welford online mean/variance update.
        Count++;
        var delta = perOpNs - Mean;
        Mean += delta / Count;
        var delta2 = perOpNs - Mean;
        _m2 += delta * delta2;

        if (Count >= _maxSamples)
        {
            ComputeHalfWidth();
            Resolved = true;
            StopReason = SampleStopReason.MaxCeiling;
            return true;
        }

        if (Count >= _minSamples && Count % _cadence == 0
                                 && ComputeHalfWidth() && AchievedRelativeHalfWidth < _ciTarget)
        {
            Resolved = true;
            StopReason = SampleStopReason.CiTargetMet;
            return true;
        }

        return false;
    }

    private bool ComputeHalfWidth()
    {
        if (Count < 2 || Mean <= 0)
        {
            AchievedRelativeHalfWidth = double.PositiveInfinity;
            return false;
        }

        var standardError = StandardDeviation / Math.Sqrt(Count);
        var t = StudentT.CriticalValue(_confidenceLevel, (int)(Count - 1));

        if (double.IsNaN(t))
        {
            AchievedRelativeHalfWidth = double.PositiveInfinity;
            return false;
        }

        AchievedRelativeHalfWidth = t * standardError / Mean;
        _halfWidthSeries.Add(AchievedRelativeHalfWidth);
        return true;
    }
}
