namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Pure stop-gate logic for the measurement phase, the counterpart to <see cref="WarmupGates" />
///     and deliberately shaped the same way so the two decision tables read alike. The CI-width rule
///     (<see cref="CiWidthDetector" />) decides <em>whether the interval is narrow enough</em>; these
///     gates decide <em>whether it is honest to stop</em> even once it is.
///     <para>
///         Two independent concerns live here. <see cref="TimeFloorMet" /> keeps a cheap body from
///         stopping after a handful of samples just because its interval happens to look narrow.
///         <see cref="IsSteady" /> refuses a stop when the stream is still moving, which is the failure
///         mode that makes a run-to-run discrepancy hardest to spot: a JIT tier-up landing inside the
///         measurement window produces a step change, and the CI-on-the-mean rule will happily report
///         a tight interval straight across it.
///     </para>
/// </summary>
internal static class MeasurementGates
{
    /// <summary>
    ///     How many standard errors the two half-means must differ by before the drift gate treats the
    ///     gap as real rather than sampling noise.
    ///     <para>
    ///         4.0 is safe (p ≈ 6e-5) because the gate is evaluated exactly <em>once per would-be
    ///         stop</em>, not once per cadence check. Evaluating it on every cadence check would mean
    ///         thousands of tests per benchmark and a false-positive rate to match; one test per stop
    ///         collapses that multiple-testing problem entirely.
    ///     </para>
    /// </summary>
    internal const double DefaultSigmaTolerance = 4.0;

    /// <summary>
    ///     Whether the measured-sample floor is satisfied: the phase has either spanned
    ///     <paramref name="minMeasurementTimeNs" /> of in-body time or reached
    ///     <paramref name="sampleCeiling" /> samples.
    ///     <para>
    ///         The floor is checked against <em>measured</em> time rather than a per-sample estimate on
    ///         purpose. An estimate is unavailable on exactly the paths that need it least forgiving -
    ///         a pinned <see cref="MeasurementOptions.OpsPerSample" /> together with pinned or zero
    ///         warmup leaves calibration and the plateau detector with nothing to report - so gating on
    ///         the accumulator is both exact and universal.
    ///     </para>
    /// </summary>
    /// <param name="count">Samples collected so far in the current measurement attempt.</param>
    /// <param name="measurementNs">Accumulated in-body measurement nanoseconds for that attempt.</param>
    /// <param name="minMeasurementTimeNs">The duration floor; <c>0</c> disables it.</param>
    /// <param name="sampleCeiling">
    ///     The count at which the floor gives up waiting for the duration, so a nano-scale body cannot
    ///     chase a duration it can never reach. Pass the effective
    ///     <see cref="AutoTuneOptions.MaxSamples" /> (see <see cref="ResolveTimeFloorCeiling" />).
    /// </param>
    public static bool TimeFloorMet(long count, double measurementNs, double minMeasurementTimeNs, int sampleCeiling)
    {
        if (minMeasurementTimeNs <= 0)
            return true;

        return measurementNs >= minMeasurementTimeNs || count >= sampleCeiling;
    }

    /// <summary>
    ///     Resolves the sample count at which <see cref="TimeFloorMet" /> stops waiting for the duration
    ///     floor: the effective sample ceiling, so the invariant is simply <em>measurement spans at least
    ///     <see cref="AutoTuneOptions.MinMeasurementTime" />, or reaches
    ///     <see cref="AutoTuneOptions.MaxSamples" /> samples, whichever comes first</em>.
    ///     <para>
    ///         A smaller cap (an earlier fraction of the ceiling) looks thriftier but silently defeats
    ///         the floor across a wide middle band of body speeds. With a cap at a tenth of the ceiling,
    ///         any body slower than <c>MinMeasurementTime / (MaxSamples / 10)</c> - about 200 µs per
    ///         sample at the defaults - is released long before the duration is reached, which is exactly
    ///         the range where a body can still be mid-tier-up. `MaxSamples` already bounds the loop, so
    ///         no separate cap is needed to keep a run finite.
    ///     </para>
    /// </summary>
    public static int ResolveTimeFloorCeiling(int minSamples, int maxSamples)
        => Math.Max(minSamples, maxSamples);

    /// <summary>
    ///     Whether the collected stream looks stationary, comparing the mean of its first half against
    ///     the mean of its second half. Returns <c>true</c> (safe to stop) unless the gap is
    ///     <em>both</em> relatively large and statistically real.
    ///     <para>
    ///         Both conditions are required, and neither works alone. A bare relative rule
    ///         false-positives catastrophically on heavy-tailed bodies - at a coefficient of variation
    ///         of 580% and n = 200, the half-mean gap from pure noise averages well over 100%, so every
    ///         such benchmark would be flagged forever. A bare significance rule flags
    ///         statistically-real-but-irrelevant drift once n reaches the thousands, where a
    ///         sub-percent difference clears any p-value threshold. The conjunction asks the question
    ///         that actually matters: did the body move by an amount worth caring about, and is that
    ///         movement more than noise?
    ///     </para>
    /// </summary>
    /// <param name="firstHalfMean">Mean of the first half of the samples, in arrival order.</param>
    /// <param name="secondHalfMean">Mean of the second half of the samples, in arrival order.</param>
    /// <param name="count">Total samples across both halves.</param>
    /// <param name="standardDeviation">The stream's sample standard deviation.</param>
    /// <param name="relativeTolerance">
    ///     Permitted gap as a fraction of the smaller half-mean; <c>0</c> disables the gate.
    /// </param>
    /// <param name="sigmaTolerance">Standard errors of the difference the gap must exceed.</param>
    public static bool IsSteady(
        double firstHalfMean,
        double secondHalfMean,
        long count,
        double standardDeviation,
        double relativeTolerance,
        double sigmaTolerance)
    {
        // Gate disabled, or too few samples to have two halves worth comparing.
        if (relativeTolerance <= 0 || count < 2)
            return true;

        // Degenerate means carry no signal - fail open. A non-positive mean already leaves the CI
        // detector's relative half-width infinite, so it can never stop on the CI target anyway.
        if (!double.IsFinite(firstHalfMean) || !double.IsFinite(secondHalfMean)
                                           || firstHalfMean <= 0 || secondHalfMean <= 0)
        {
            return true;
        }

        var gap = Math.Abs(secondHalfMean - firstHalfMean);

        // Relative arm: measured against the smaller half-mean so a step trips the gate at the same
        // magnitude whether the body got faster or slower.
        if (gap / Math.Min(firstHalfMean, secondHalfMean) <= relativeTolerance)
            return true;

        // Statistical arm: the standard error of a difference between two means of n/2 samples each is
        // sd * sqrt(2/(n/2)) = sd * 2/sqrt(n).
        if (!double.IsFinite(standardDeviation) || standardDeviation <= 0)
            return false;

        var standardErrorOfDifference = standardDeviation * 2.0 / Math.Sqrt(count);
        return gap <= sigmaTolerance * standardErrorOfDifference;
    }

    /// <summary>
    ///     The relative gap between the two half-means, as a fraction of the smaller one, or <c>0</c>
    ///     when either is degenerate. Reported as a diagnostic so a tight interval sitting next to a
    ///     large drift is visible rather than silently trusted.
    /// </summary>
    public static double SplitHalfDrift(double firstHalfMean, double secondHalfMean)
    {
        if (!double.IsFinite(firstHalfMean) || !double.IsFinite(secondHalfMean)
                                           || firstHalfMean <= 0 || secondHalfMean <= 0)
        {
            return 0.0;
        }

        return Math.Abs(secondHalfMean - firstHalfMean) / Math.Min(firstHalfMean, secondHalfMean);
    }
}

/// <summary>
///     Maintains the first- and second-half means of a growing sample stream in O(1) per sample, for
///     <see cref="MeasurementGates.IsSteady" />.
///     <para>
///         Recomputing the halves at each stop check would be O(n) on a list that can reach the sample
///         ceiling, on the measurement hot path. The incremental form is exact instead of approximate:
///         the split point <c>n / 2</c> advances by one every <em>second</em> sample, so at most one
///         element ever migrates from the second half to the first, and it can be moved by adjusting
///         two running sums.
///     </para>
/// </summary>
/// <remarks>
///     Constructed over the caller's live sample list rather than copying it, so the tracker sees
///     appends the caller makes. The caller must <see cref="Add" /> each sample after appending it and
///     <see cref="Reset" /> whenever it clears the list.
/// </remarks>
internal sealed class SplitHalfTracker
{
    private readonly List<double> _samples;
    private double _firstHalfSum;
    private int _firstHalfCount;
    private double _sum;

    public SplitHalfTracker(List<double> samples) => _samples = samples;

    /// <summary>Samples fed so far.</summary>
    public int Count { get; private set; }

    /// <summary>Mean of the first <c>Count / 2</c> samples, or <c>0</c> before two samples have arrived.</summary>
    public double FirstHalfMean => _firstHalfCount > 0 ? _firstHalfSum / _firstHalfCount : 0.0;

    /// <summary>Mean of the remaining samples, or <c>0</c> before two samples have arrived.</summary>
    public double SecondHalfMean
    {
        get
        {
            var secondHalfCount = Count - _firstHalfCount;
            return secondHalfCount > 0 ? (_sum - _firstHalfSum) / secondHalfCount : 0.0;
        }
    }

    /// <summary>
    ///     Reports one sample, which the caller must already have appended to the backing list.
    /// </summary>
    public void Add(double value)
    {
        _sum += value;
        Count++;

        // The split point is Count / 2, which grows by one on every second sample. When it does, the
        // element now at index _firstHalfCount crosses from the second half into the first.
        var half = Count / 2;

        if (half > _firstHalfCount)
        {
            _firstHalfSum += _samples[_firstHalfCount];
            _firstHalfCount++;
        }
    }

    /// <summary>Clears all state, for when the caller discards the collected samples and restarts.</summary>
    public void Reset()
    {
        _sum = 0;
        _firstHalfSum = 0;
        _firstHalfCount = 0;
        Count = 0;
    }
}
