namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     The thresholds <c>BenchmarkAssert.Validate</c> checks a finished
///     <see cref="BenchmarkResult" /> against.
/// </summary>
/// <remarks>
///     <para>
///         This is the assert pattern's option bag, not the attribute pattern's. It is an ordinary
///         object rather than attribute metadata, so an unset threshold is <c>null</c> here instead
///         of the <see cref="IPerformanceThresholds.Unset" /> sentinel the attributes are forced to
///         use.
///     </para>
///     <para>
///         It carries only what can be decided from a result that has already been measured. There
///         is deliberately no sample count (the measurement is over) and no slowdown ratio (a ratio
///         needs a reference measurement this type cannot take) - those live on
///         <see cref="IPerformanceThresholds" />, where the attribute owns the measurement.
///     </para>
/// </remarks>
public sealed class PerformanceThresholds
{
    /// <summary>Maximum mean time per operation in nanoseconds, or <c>null</c> to not check it.</summary>
    public double? MaxMeanNs { get; init; }

    /// <summary>
    ///     Maximum median time per operation in nanoseconds, or <c>null</c> to not check it. The
    ///     median is what the reports lead with; prefer it over <see cref="MaxMeanNs" /> unless the
    ///     average is genuinely what is being bounded.
    /// </summary>
    public double? MaxMedianNs { get; init; }

    /// <summary>Maximum 95th-percentile time per operation in nanoseconds, or <c>null</c> to not check it.</summary>
    public double? MaxP95Ns { get; init; }

    /// <summary>Maximum mean bytes allocated per operation, or <c>null</c> to not check it.</summary>
    public long? MaxAllocatedBytes { get; init; }

    /// <summary>
    ///     How far past an absolute threshold a measurement may land before the check fails, as a
    ///     multiplier of the threshold. <c>1.0</c> - the default - fails at the threshold exactly.
    /// </summary>
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;
}
