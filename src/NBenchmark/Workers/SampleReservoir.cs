namespace NBenchmark.Workers;

/// <summary>
///     Reduces a benchmark's raw sample array to a bounded, representative subset before it crosses
///     the process boundary.
/// </summary>
/// <remarks>
///     <para>
///         A worker measures up to <see cref="MeasurementOptions.MaxIterations" /> samples, so an
///         untruncated array is 800 KB of JSON-encoded doubles per benchmark - which is why the frame
///         ceiling had to be set at 64 MB to accommodate it. The full array is not what the
///         coordinator needs: the worker already computed every statistic from it locally, and what
///         crosses is only used for significance testing and the Console density sparkline. Both are
///         distribution properties, and a few thousand samples describe a distribution as well as a
///         hundred thousand do.
///     </para>
///     <para>
///         The subset is drawn <b>uniformly at random</b> rather than by taking a prefix or every
///         k-th sample. A prefix is not a sample of the distribution at all - it is the first
///         fraction of the run, which is exactly where residual warmup effects live. Systematic
///         every-k-th selection is unbiased in the mean but aliases against anything periodic in the
///         measurement, and periodic structure is common here: a GC every n iterations, a buffer that
///         wraps, a timer that ticks. Random selection cannot alias.
///     </para>
///     <para>
///         Selection is seeded from the run's own seed, so a re-run with the same seed ships the same
///         subset. A benchmarking tool that produced different numbers on a repeat of the same
///         configuration would be indistinguishable from one with a measurement bug.
///     </para>
/// </remarks>
internal static class SampleReservoir
{
    /// <summary>
    ///     Draws at most <paramref name="capacity" /> samples, keeping them in measurement order and
    ///     remapping <paramref name="trimmedOrdinals" /> onto the reduced array.
    /// </summary>
    /// <remarks>
    ///     Remapping is not optional. <see cref="BenchmarkResult.TrimmedOrdinals" /> holds positions
    ///     <i>into</i> <see cref="BenchmarkResult.RawSamples" />, and the Console reporter uses them
    ///     to mark which samples the outlier detector discarded. Shipping a reduced array beside
    ///     ordinals computed against the full one would not fail - it would quietly mark the wrong
    ///     samples, and the marks would still look plausible.
    /// </remarks>
    public static (double[] Samples, int[] TrimmedOrdinals) Reduce(
        double[] samples,
        IReadOnlyList<int> trimmedOrdinals,
        int capacity,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(trimmedOrdinals);

        if (capacity <= MeasurementOptions.UnboundedRawSamples || samples.Length <= capacity)
            return (samples, [.. trimmedOrdinals]);

        var kept = SelectIndices(samples.Length, capacity, seed);

        // Ascending, so the kept samples stay in measurement order - the order RawSamples is
        // documented to be in, and the order the sparkline reads left to right.
        Array.Sort(kept);

        var reduced = new double[kept.Length];

        for (var i = 0; i < kept.Length; i++)
            reduced[i] = samples[kept[i]];

        return (reduced, RemapOrdinals(kept, trimmedOrdinals));
    }

    /// <summary>
    ///     A uniform random sample of <paramref name="capacity" /> distinct indices from
    ///     <c>[0, count)</c>, by partial Fisher-Yates.
    /// </summary>
    /// <remarks>
    ///     The scratch array costs one int per sample - 400 KB at the iteration ceiling - and is
    ///     allocated only on the path that is about to discard far more than that from the wire.
    ///     Rejection sampling would avoid it but degrades badly as the capacity approaches the count,
    ///     which is precisely the boundary case.
    /// </remarks>
    private static int[] SelectIndices(int count, int capacity, int seed)
    {
        var indices = new int[count];

        for (var i = 0; i < count; i++)
            indices[i] = i;

        var random = new Random(seed);

        for (var i = 0; i < capacity; i++)
        {
            var j = random.Next(i, count);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        return indices[..capacity];
    }

    /// <summary>
    ///     Rewrites trimmed ordinals as positions in the reduced array, dropping those whose sample
    ///     was not kept.
    /// </summary>
    /// <remarks>
    ///     A trimmed sample that was not selected simply disappears along with its mark, which is
    ///     correct: the reduced array is a sample of the run, and its trimmed marks are the
    ///     corresponding sample of the trimmed set. The trim <i>counts</i> a reader sees come from
    ///     <see cref="BenchmarkResult.OutliersRemoved" />, computed by the worker over the full array
    ///     and unaffected by any of this.
    /// </remarks>
    private static int[] RemapOrdinals(int[] keptAscending, IReadOnlyList<int> trimmedOrdinals)
    {
        if (trimmedOrdinals.Count == 0)
            return [];

        var trimmed = new HashSet<int>(trimmedOrdinals);
        var remapped = new List<int>();

        for (var position = 0; position < keptAscending.Length; position++)
        {
            if (trimmed.Contains(keptAscending[position]))
                remapped.Add(position);
        }

        return [.. remapped];
    }
}
