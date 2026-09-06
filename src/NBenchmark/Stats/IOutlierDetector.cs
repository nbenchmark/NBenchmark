namespace NBenchmark.Stats;

/// <summary>
///     Decides which raw timing samples are inliers (kept) and which are outliers
///     (discarded) before descriptive statistics are computed.
///     <para>
///         Implement this interface to plug a custom trimming strategy into the engine
///         (for example a tail-preserving rule for high-frequency-trading latency, or a
///         domain-specific rejection filter). Register it with
///         <see cref="MeasurementOptions.OutlierDetector" />,
///         <c>BenchmarkSuite.WithOutlierDetector(...)</c>, or
///         <c>BenchmarkHarness.WithOptions(...)</c>. The built-in
///         <see cref="OutlierMode" /> values map onto the detectors in
///         <see cref="OutlierDetectors" />.
///     </para>
/// </summary>
public interface IOutlierDetector
{
    /// <summary>
    ///     A short, human-readable label shown in reports (for example
    ///     <c>"IQR fence (1.5×)"</c> or <c>"MAD (3×)"</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Partitions <paramref name="sortedSamples" /> into kept (inlier) and discarded
    ///     (outlier) sets.
    ///     <para>
    ///         <paramref name="sortedSamples" /> is provided already sorted ascending, and is a
    ///         <see cref="ReadOnlySpan{T}" /> over the engine's own buffer so a detector cannot
    ///         reorder or rescale the samples it was asked to judge. Implementations must return
    ///         <see cref="OutlierClassification.Kept" /> sorted ascending as well (filtering
    ///         a sorted input preserves order, so simply keeping a subset is sufficient).
    ///         If a rule would discard every sample, return all samples unchanged so the
    ///         engine always has data to summarize.
    ///     </para>
    /// </summary>
    public OutlierClassification Classify(ReadOnlySpan<double> sortedSamples);
}

/// <summary>
///     The result of an <see cref="IOutlierDetector" />: the kept and discarded samples,
///     plus optional fence boundaries for reporting (used by fence-based detectors such as
///     <see cref="IqrFenceOutlierDetector" /> and <see cref="MadOutlierDetector" />).
/// </summary>
public sealed record OutlierClassification
{
    /// <summary>The inlier samples to feed into the statistics summary, sorted ascending.</summary>
    public required ReadOnlyMemory<double> Kept { get; init; }

    /// <summary>The samples rejected as outliers, sorted ascending.</summary>
    public required ReadOnlyMemory<double> Discarded { get; init; }

    /// <summary>The lower rejection boundary, when the detector is fence-based; otherwise <c>null</c>.</summary>
    public double? LowerFenceNs { get; init; }

    /// <summary>The upper rejection boundary, when the detector is fence-based; otherwise <c>null</c>.</summary>
    public double? UpperFenceNs { get; init; }

    /// <summary>Convenience factory that keeps every sample (no trimming).</summary>
    public static OutlierClassification KeepAll(ReadOnlySpan<double> sortedSamples) =>
        new() { Kept = sortedSamples.ToArray(), Discarded = ReadOnlyMemory<double>.Empty };
}
