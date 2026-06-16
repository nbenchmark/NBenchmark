namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Resolves how many back-to-back body invocations make up one timed sample (<c>K</c>) so
///     that fixed timer overhead is amortised away on fast bodies. A doubling search: starting at
///     <c>K = 1</c>, each timed sample is fed back; while a sample is shorter than the target
///     duration and <c>K</c> is below the ceiling, <c>K</c> doubles and another sample is timed.
/// </summary>
/// <remarks>
///     Pure state machine; no hot-path allocation. Samples timed during calibration double as
///     warmup, so the invocations are not wasted.
/// </remarks>
internal sealed class OpCountCalibrator
{
    private readonly double _targetSampleNs;
    private readonly int _maxOps;

    public OpCountCalibrator(double targetSampleDurationNs, int maxOpsPerSample)
    {
        _targetSampleNs = targetSampleDurationNs;
        _maxOps = Math.Max(1, maxOpsPerSample);
    }

    /// <summary>The current (and, once <see cref="Resolved" />, final) ops-per-sample count.</summary>
    public int OpsPerSample { get; private set; } = 1;

    /// <summary>Whether calibration has settled on a final <see cref="OpsPerSample" />.</summary>
    public bool Resolved { get; private set; }

    /// <summary>
    ///     Reports the elapsed nanoseconds of a sample timed at the current
    ///     <see cref="OpsPerSample" />. Returns <c>true</c> when calibration is resolved (the
    ///     caller should stop probing and fix <see cref="OpsPerSample" />); <c>false</c> when
    ///     <see cref="OpsPerSample" /> has been doubled and another sample should be timed.
    /// </summary>
    public bool Feed(double sampleNs)
    {
        if (Resolved)
            return true;

        if (sampleNs >= _targetSampleNs || OpsPerSample >= _maxOps)
        {
            Resolved = true;
            return true;
        }

        OpsPerSample = (int)Math.Min((long)OpsPerSample * 2, _maxOps);
        return false;
    }
}
