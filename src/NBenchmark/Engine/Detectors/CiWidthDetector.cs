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
///     <para>
///         <b>Optional stopping, and why nothing here corrects for it.</b> The stop rule evaluates
///         the half-width on a cadence and stops at the first crossing, so each evaluation is a
///         "look" at the accumulating data. A CI computed at the nominal confidence level at the
///         stopping look no longer has its nominal coverage, because the loop stopped precisely when
///         the interval happened to be narrow - the classic optional-stopping bias. The reported
///         interval is <b>not</b> corrected for it. A Bonferroni widening over <see cref="LookCount" />
///         was implemented and withdrawn: at 2,000 samples with the default cadence the look count
///         is around 250, giving <c>t_eff</c> near 3.7 against 1.96, and the looks are so heavily
///         correlated - each adds a batch to a set already thousands strong - that Bonferroni
///         over-corrects severely for the actual bias. A user who asked for a ±2.5% target would be
///         shown ±5-6%, which is not a more honest number, only a larger one. A group-sequential
///         boundary (Pocock, O'Brien-Fleming) or an always-valid confidence sequence is the right
///         instrument and is tracked as its own piece of work; the interval reports the Winsorized
///         (Yuen) precision of the trimmed mean and nothing more. <see cref="HalfWidthSeries" /> and
///         <see cref="LookCount" /> are surfaced on the diagnostic so the number of looks is at
///         least visible to a reader who cares.
///     </para>
/// </remarks>
internal sealed class CiWidthDetector
{
    // Pre-sized to avoid the first few backing-array reallocations during short runs. The
    // series grows at evaluation cadence (every BatchSize samples past MinSamples), not per
    // sample, so even a pessimistic upper bound (MaxSamples / BatchSize = 12,500) is ~100 KiB.
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

    /// <summary>
    ///     The number of half-width evaluations performed - the "looks" at the accumulating data
    ///     that drive the optional-stopping bias. Each successful evaluation appends one entry to
    ///     <see cref="HalfWidthSeries" />, so this is the count of cadence/ceiling checks that
    ///     produced a finite half-width. Nothing widens the reported interval over it today; it is
    ///     the input a sequential correction would need, and the measure of how much correction the
    ///     run would warrant.
    /// </summary>
    public int LookCount => _halfWidthSeries.Count;

    /// <summary>The number of measured samples fed so far.</summary>
    public long Count { get; private set; }

    /// <summary>The running mean of the fed samples.</summary>
    public double Mean { get; private set; }

    /// <summary>The running sample standard deviation (Bessel-corrected) of the fed samples.</summary>
    public double StandardDeviation => Count >= 2 ? Math.Sqrt(_m2 / (Count - 1)) : 0.0;

    /// <summary>
    ///     The running coefficient of variation (standard deviation / mean), or <see cref="double.NaN" />
    ///     before a positive mean exists. Free from the Welford state, and the number that explains a
    ///     ceiling stop: the CI-on-the-mean rule needs samples proportional to the <em>square</em> of
    ///     this, so a body with a CV in the hundreds of percent can never converge.
    /// </summary>
    public double CoefficientOfVariation => Mean > 0 ? StandardDeviation / Mean : double.NaN;

    /// <summary>
    ///     Reports the per-op nanoseconds of one measured sample. Returns <c>true</c> when the
    ///     measurement phase is resolved (the caller should stop collecting samples).
    /// </summary>
    /// <param name="perOpNs">The sample's per-op nanoseconds.</param>
    /// <param name="stopAllowed">
    ///     Whether an outside floor (see <see cref="MeasurementGates.TimeFloorMet" />) currently permits
    ///     stopping on the CI target. When <c>false</c> the detector keeps accumulating and still
    ///     records the achieved half-width, but does not resolve - so the composed floor stays a caller
    ///     policy while the accumulator stays here. The sample ceiling is unaffected: blocking there
    ///     would spin forever.
    /// </param>
    public bool Feed(double perOpNs, bool stopAllowed)
    {
        if (Resolved)
            return true;

        // Welford online mean/variance update.
        Count++;
        var delta = perOpNs - Mean;
        Mean += delta / Count;
        var delta2 = perOpNs - Mean;
        _m2 += delta * delta2;

        // The CI target is evaluated before the ceiling so that a run whose final sample both reaches
        // MaxSamples and meets the target reports CiTargetMet rather than a ceiling stop - the target
        // was met, and the warning attached to a ceiling stop would be wrong. Also keeps the half-width
        // computed exactly once per sample.
        var atCadence = Count >= _minSamples && Count % _cadence == 0;
        var atCeiling = Count >= _maxSamples;

        if (atCadence || atCeiling)
        {
            var computed = ComputeHalfWidth();

            if (stopAllowed && computed && AchievedRelativeHalfWidth < _ciTarget)
            {
                Resolved = true;
                StopReason = SampleStopReason.CiTargetMet;
                return true;
            }

            if (atCeiling)
            {
                Resolved = true;
                StopReason = SampleStopReason.MaxCeiling;
                return true;
            }
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
