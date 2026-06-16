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
    private readonly double _confidenceLevel;
    private readonly double _ciTarget;
    private readonly int _minSamples;
    private readonly int _maxSamples;
    private readonly int _cadence;

    private long _n;
    private double _mean;
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

    /// <summary>The number of measured samples fed so far.</summary>
    public long Count => _n;

    /// <summary>The running mean of the fed samples.</summary>
    public double Mean => _mean;

    /// <summary>The running sample standard deviation (Bessel-corrected) of the fed samples.</summary>
    public double StandardDeviation => _n >= 2 ? Math.Sqrt(_m2 / (_n - 1)) : 0.0;

    /// <summary>
    ///     Reports the per-op nanoseconds of one measured sample. Returns <c>true</c> when the
    ///     measurement phase is resolved (the caller should stop collecting samples).
    /// </summary>
    public bool Feed(double perOpNs)
    {
        if (Resolved)
            return true;

        // Welford online mean/variance update.
        _n++;
        var delta = perOpNs - _mean;
        _mean += delta / _n;
        var delta2 = perOpNs - _mean;
        _m2 += delta * delta2;

        if (_n >= _maxSamples)
        {
            ComputeHalfWidth();
            Resolved = true;
            StopReason = SampleStopReason.MaxCeiling;
            return true;
        }

        if (_n >= _minSamples && _n % _cadence == 0
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
        if (_n < 2 || _mean <= 0)
        {
            AchievedRelativeHalfWidth = double.PositiveInfinity;
            return false;
        }

        var standardError = StandardDeviation / Math.Sqrt(_n);
        var t = StudentT.CriticalValue(_confidenceLevel, (int)(_n - 1));

        if (double.IsNaN(t))
        {
            AchievedRelativeHalfWidth = double.PositiveInfinity;
            return false;
        }

        AchievedRelativeHalfWidth = t * standardError / _mean;
        return true;
    }
}
