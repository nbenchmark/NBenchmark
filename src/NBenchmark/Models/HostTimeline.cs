namespace NBenchmark;

/// <summary>
///     Where in the run this benchmark was measured, and how fast the host was at that point.
///     Produced by the host drift canary (<see cref="DriftCanaryOptions" />); <c>null</c> when the
///     canary was off or could not produce a reading.
/// </summary>
/// <remarks>
///     <para>
///         The canary runs the same fixed, deterministic work at every benchmark boundary, so the
///         only thing that can change its timing is the machine. This record pairs the reading
///         taken immediately before a benchmark with the one taken immediately after it, which is
///         the tightest bracket available on "how fast was the host while this row was being
///         measured".
///     </para>
///     <para>
///         <see cref="RelativeToRunStart" /> is the number to compare between rows. Two benchmarks
///         measured when the host was equally fast agree on it however slow the host was; two
///         benchmarks separated by a thermal ramp do not, and the gap between them is a lower
///         bound on how much of any difference between their medians is the machine rather than
///         the code.
///     </para>
/// </remarks>
public sealed record HostTimeline
{
    /// <summary>
    ///     The canary reading taken immediately before this benchmark, in nanoseconds per sample.
    ///     An absolute number with no meaning outside this process - only its ratio to the other
    ///     readings in the same run is interpretable.
    /// </summary>
    public double BeforeNs { get; init; }

    /// <summary>
    ///     The canary reading taken immediately after this benchmark, in nanoseconds per sample.
    /// </summary>
    public double AfterNs { get; init; }

    /// <summary>
    ///     The mean of <see cref="BeforeNs" /> and <see cref="AfterNs" />, as a multiple of the
    ///     run's first reading. <c>1.0</c> means the host was as fast here as it was at the start
    ///     of the run; <c>1.07</c> means the same fixed work took 7% longer, so a benchmark
    ///     measured here is reported roughly 7% slower for reasons that have nothing to do with
    ///     the code.
    /// </summary>
    public double RelativeToRunStart { get; init; }

    /// <summary>
    ///     How many benchmarks had completed when this one started. A <see cref="double" /> rather
    ///     than an <see cref="int" /> because a multi-launch row averages the position over its
    ///     launches, which run in independent random orders.
    /// </summary>
    public double CompletedBenchmarks { get; init; }
}
