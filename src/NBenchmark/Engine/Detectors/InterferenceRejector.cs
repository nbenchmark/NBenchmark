namespace NBenchmark.Engine.Detectors;

/// <summary>
///     The evidence-based interference-rejection pre-stage: discards samples the OS is known to
///     have preempted, before <c>OutlierTrim</c> ever sees the stream.
/// </summary>
/// <remarks>
///     <para>
///         Every other discard decision in the engine infers preemption from the timing value alone.
///         This one does not infer: it reads the per-sample CPU-occupancy ratio
///         <c>r_i = cpuDelta_i / wallDelta_i</c> that <c>AdaptiveLoop</c> already bracketed each
///         sample with, normalizes it against this benchmark's own median <c>r̃</c>, and rejects a
///         sample when <c>r_i &lt; threshold * r̃</c> - a sample that held the CPU for materially less
///         of its wall-clock window than a typical sample did was, as a matter of fact, not running
///         the whole time.
///     </para>
///     <para>
///         Deliberately a separate stage from <c>IOutlierDetector.Classify</c> rather than an
///         extension of it: "was this sample valid?" (evidence: CPU occupancy) and "is this value an
///         outlier?" (evidence: the timing distribution) are different questions answered from
///         different evidence, and keeping them apart means every built-in and user-supplied detector
///         composes with this filter for free.
///     </para>
///     <para>
///         The median is computed only from samples with a <em>known</em> occupancy ratio - an async
///         body that resumed on a different thread mid-sample makes the thread-CPU clock meaningless
///         for that sample, so <c>AdaptiveLoop</c> reports it as <see cref="double.NaN" /> rather than
///         a wrong number. A NaN entry is never rejected and never contributes to the median: unknown
///         occupancy is not evidence of interference. When too few samples have a known ratio to
///         trust a median, the whole stage disables itself for this benchmark rather than rejecting
///         from a handful of readings, and says so.
///     </para>
/// </remarks>
internal static class InterferenceRejector
{
    /// <summary>
    ///     The fewest raw samples the stage will compute a median from. Below this, two or three
    ///     atypically slow (or fast) readings can swing a median far enough to falsely accuse an
    ///     ordinary sample - the same reasoning that makes a two- or three-point sample unusable for
    ///     any other robust statistic in this engine. A benchmark measured with this few samples is
    ///     already reporting an unreliable interval for reasons unrelated to interference (see
    ///     <c>AutoTuneOptions.MinSamples</c>), so disabling the filter here costs nothing real.
    /// </summary>
    private const int MinTimingsForRejection = 10;

    /// <summary>The result of one call to <see cref="Reject" />.</summary>
    /// <param name="SurvivingTimings">
    ///     <c>timings</c> with rejected samples removed, in arrival order.
    /// </param>
    /// <param name="SurvivingOriginalIndices">
    ///     For each entry in <see cref="SurvivingTimings" />, its position in the original input
    ///     array - the map a caller needs to translate ordinals computed against the surviving array
    ///     (e.g. <c>OutlierTrim</c>'s <c>TrimmedOrdinals</c>) back onto the original sample stream.
    /// </param>
    /// <param name="RejectedOriginalIndices">
    ///     Positions in the original input array of every rejected sample, ascending.
    /// </param>
    /// <param name="MedianOccupancyRatio">
    ///     The benchmark's own median occupancy ratio the rejection threshold was computed against,
    ///     or <c>null</c> when the stage did not run (disabled, unavailable, or too few known-occupancy
    ///     samples).
    /// </param>
    /// <param name="DisabledReason">
    ///     Why the stage did not reject anything on its own initiative (as opposed to genuinely
    ///     finding nothing to reject), or <c>null</c> when it ran normally. Set when too few samples
    ///     had a known occupancy ratio - the "most samples thread-hopped" case - so the caller can
    ///     report why rather than silently returning a zero count.
    /// </param>
    internal readonly record struct Result(
        double[] SurvivingTimings,
        int[] SurvivingOriginalIndices,
        int[] RejectedOriginalIndices,
        double? MedianOccupancyRatio,
        string? DisabledReason);

    /// <summary>
    ///     Applies interference rejection to <paramref name="timings" /> using the parallel
    ///     <paramref name="occupancy" /> array (same length, <see cref="double.NaN" /> for unknown
    ///     occupancy), per <paramref name="options" />. Returns the input unchanged (as a same-length
    ///     "surviving" set with an identity index map) when the stage is off, the occupancy array is
    ///     absent or mismatched in length, or too few samples carry a known ratio.
    /// </summary>
    public static Result Reject(double[] timings, double[]? occupancy, InterferenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || occupancy is null || occupancy.Length != timings.Length || timings.Length == 0)
            return NoOp(timings);

        if (timings.Length < MinTimingsForRejection)
        {
            return NoOp(timings) with
            {
                DisabledReason = $"only {timings.Length} sample(s) were measured - below the "
                                  + $"{MinTimingsForRejection} needed to trust a median occupancy ratio",
            };
        }

        var known = new List<double>(occupancy.Length);

        foreach (var ratio in occupancy)
        {
            if (double.IsFinite(ratio) && ratio > 0)
                known.Add(ratio);
        }

        // Below this fraction, too little of the stream has a trustworthy occupancy reading to
        // compute a median worth rejecting against - the common cause is an async body whose
        // continuations mostly resumed on a different thread. Disabling here rather than rejecting
        // from a handful of readings is the "reports why instead of degrading silently" contract.
        if (known.Count < timings.Length * options.KnownSampleFraction)
        {
            return NoOp(timings) with
            {
                DisabledReason = known.Count == 0
                    ? "no sample produced a known CPU-occupancy reading (the thread-CPU clock is "
                      + "unavailable, or every sample's continuation resumed on a different thread)"
                    : $"only {known.Count} of {timings.Length} samples had a known CPU-occupancy "
                      + "reading - most likely because an async continuation resumed on a different "
                      + "thread - below the minimum needed to trust a median occupancy ratio",
            };
        }

        known.Sort();
        var median = Median(known);

        if (median <= 0)
            return NoOp(timings) with { DisabledReason = "the median CPU-occupancy ratio was zero" };

        var threshold = options.RejectionThreshold * median;

        var surviving = new List<double>(timings.Length);
        var survivingIndices = new List<int>(timings.Length);
        var rejectedIndices = new List<int>();

        for (var i = 0; i < timings.Length; i++)
        {
            var ratio = occupancy[i];

            // Unknown occupancy (NaN, or non-positive) is never rejected - it is not evidence of
            // interference, just an absence of evidence. Only a known ratio below threshold counts.
            if (double.IsFinite(ratio) && ratio > 0 && ratio < threshold)
            {
                rejectedIndices.Add(i);
                continue;
            }

            surviving.Add(timings[i]);
            survivingIndices.Add(i);
        }

        return new Result(
            surviving.ToArray(),
            survivingIndices.ToArray(),
            rejectedIndices.ToArray(),
            median,
            null);
    }

    private static Result NoOp(double[] timings)
    {
        var identity = new int[timings.Length];

        for (var i = 0; i < identity.Length; i++)
        {
            identity[i] = i;
        }

        return new Result(timings, identity, [], null, null);
    }

    private static double Median(List<double> sorted)
    {
        var n = sorted.Count;
        var mid = n / 2;

        return (n & 1) == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
