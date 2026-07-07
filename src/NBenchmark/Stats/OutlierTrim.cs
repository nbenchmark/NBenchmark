namespace NBenchmark.Stats;

/// <summary>
///     Bridges the engine to an <see cref="IOutlierDetector" />: it computes the quartile
///     descriptive statistics (Q1/Q3/IQR) that every report shows - independent of the
///     trimming strategy - and delegates the keep/discard decision to the detector.
/// </summary>
/// <remarks>
///     <para>
///         The <see cref="TrimDetailed(double[], IOutlierDetector)" /> overload does not
///         mutate <paramref name="timings" />: it sorts a copy for the detector and
///         preserves the original order so callers can re-use the input. The returned
///         <see cref="TrimResult.Kept" /> and <see cref="TrimResult.Discarded" /> arrays
///         are sorted ascending (the detector contract), while
///         <see cref="TrimResult.TrimmedOrdinals" /> maps the discarded values back to
///         their positions in the original <paramref name="timings" /> array, so a
///         consumer can flag which raw samples were outliers without re-running the
///         detector.
///     </para>
/// </remarks>
public static class OutlierTrim
{
    /// <summary>
    ///     Trims <paramref name="timings" /> and returns only the kept (inlier) samples.
    ///     Ordinal information is discarded; use <see cref="TrimDetailed" /> when the
    ///     discard set or fence boundaries are needed.
    /// </summary>
    public static double[] Trim(double[] timings, OutlierMode mode) => TrimDetailed(timings, mode).Kept;

    /// <summary>Convenience overload that resolves the built-in detector for <paramref name="mode" />.</summary>
    public static TrimResult TrimDetailed(double[] timings, OutlierMode mode) =>
        TrimDetailed(timings, OutlierDetectors.ForMode(mode));

    /// <summary>
    ///     Computes Q1/Q3/IQR over <paramref name="timings" />, delegates the keep/discard
    ///     decision to <paramref name="detector" />, and returns the kept and discarded
    ///     samples plus the fence boundaries and the original ordinals of every discarded
    ///     sample.
    /// </summary>
    /// <param name="timings">Raw per-op timings in arrival order. Not mutated.</param>
    /// <param name="detector">The outlier-detection strategy.</param>
    /// <remarks>
    ///     The detector contract (<see cref="IOutlierDetector.Classify" />) receives a
    ///     sorted copy and returns kept/discarded as sorted values; this method then
    ///     correlates the discarded values back to their original positions in
    ///     <paramref name="timings" />. Correlation is by sorted-position correspondence
    ///     (the detector preserves order on sorted input, so the i-th discarded value in
    ///     the sorted classification corresponds to the i-th discarded entry in the sorted
    ///     index array), which correctly handles duplicate values: every (value, original
    ///     index) pair is tracked through the sort, so two identical outliers receive
    ///     distinct ordinals.
    /// </remarks>
    public static TrimResult TrimDetailed(double[] timings, IOutlierDetector detector)
    {
        // Build a sorted copy for the detector and a parallel index array that records the
        // original position of each sorted value. Stable ordering of equal values is not
        // required for the detector (it filters by value), but it makes the ordinal
        // correlation unambiguous: the i-th element of `sorted` came from `indices[i]`.
        var sorted = (double[])timings.Clone();
        var indices = new int[timings.Length];

        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;

        Array.Sort(sorted, indices);

        var q1 = Percentile.Compute(sorted, 0.25);
        var q3 = Percentile.Compute(sorted, 0.75);
        var iqr = q3 - q1;

        var classification = detector.Classify(sorted);

        // Recover the original ordinals of every discarded sample. The detector returns
        // Discarded as a sorted subset of `sorted`; because `indices` is sorted in the
        // same order as `sorted`, we can walk both arrays in lockstep: each discarded
        // value matches the next un-consumed sorted value, and its original ordinal is
        // indices[that position]. This is O(n + d) rather than O(n*d).
        var trimmedOrdinals = RecoverDiscardedOrdinals(sorted, indices, classification.Discarded);

        return new TrimResult(
            classification.Kept,
            classification.Discarded,
            q1,
            q3,
            iqr,
            classification.LowerFence,
            classification.UpperFence,
            trimmedOrdinals);
    }

    private static int[] RecoverDiscardedOrdinals(double[] sortedValues, int[] sortedIndices, double[] discarded)
    {
        if (discarded.Length == 0)
            return [];

        var result = new int[discarded.Length];
        var writeIdx = 0;
        var scanIdx = 0;

        // Both `sortedValues` and `discarded` are sorted ascending. Walk `sortedValues`
        // once; whenever the current sorted value equals the next discarded value, record
        // its original ordinal and advance the discarded cursor. Values are compared by
        // bitwise equality (the detector partitions by the same values it received), so
        // equal values are matched in arrival-order-of-the-sorted-index, which is stable.
        for (var d = 0; d < discarded.Length; d++)
        {
            var target = discarded[d];

            while (scanIdx < sortedValues.Length)
            {
                if (sortedValues[scanIdx] == target)
                {
                    result[writeIdx++] = sortedIndices[scanIdx];
                    scanIdx++;
                    break;
                }

                scanIdx++;
            }
        }

        // If the detector collapsed duplicates (e.g. returned fewer discarded values than
        // we expected because it deduplicated), trim the result to what we actually found.
        // This keeps the invariant result.Length == discarded.Length when the detector
        // preserves duplicates, which is the contract every built-in detector satisfies.
        if (writeIdx < result.Length)
            Array.Resize(ref result, writeIdx);

        return result;
    }
}

/// <summary>
///     The output of <see cref="OutlierTrim.TrimDetailed" />: the kept and discarded sample
///     arrays, the quartile descriptive statistics, the fence boundaries (when the detector
///     is fence-based), and the original ordinals of every discarded sample.
/// </summary>
/// <param name="Kept">Inlier samples to feed into the statistics summary, sorted ascending.</param>
/// <param name="Discarded">Outlier samples rejected by the detector, sorted ascending.</param>
/// <param name="Q1">First quartile of the full (pre-trim) sample set.</param>
/// <param name="Q3">Third quartile of the full (pre-trim) sample set.</param>
/// <param name="InterquartileRange"><paramref name="Q3" /> - <paramref name="Q1" />.</param>
/// <param name="LowerFence">Lower rejection boundary when the detector is fence-based; otherwise <c>null</c>.</param>
/// <param name="UpperFence">Upper rejection boundary when the detector is fence-based; otherwise <c>null</c>.</param>
/// <param name="TrimmedOrdinals">
///     Positions in the original input array of every sample in <paramref name="Discarded" />,
///     in the same order as <paramref name="Discarded" /> (sorted ascending by value). Empty
///     when nothing was trimmed. Use this to flag raw samples as outliers without re-running
///     the detector.
/// </param>
public readonly record struct TrimResult(
    double[] Kept,
    double[] Discarded,
    double Q1,
    double Q3,
    double InterquartileRange,
    double? LowerFence,
    double? UpperFence,
    int[] TrimmedOrdinals);
