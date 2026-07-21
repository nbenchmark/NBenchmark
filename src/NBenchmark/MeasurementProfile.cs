namespace NBenchmark;

/// <summary>
///     Controls how garbage collection interacts with the measurement loop. The profile is a
///     bundle that drives the per-iteration Gen0 GC and the pre-measurement full GC. Allocation
///     tracking and the between-benchmark full GC are on for both profiles (allocation tracking is
///     measured outside the timed window, so it costs nothing; the between-benchmark GC keeps one
///     benchmark's leftover heap from biasing the next). Each behaviour can be overridden
///     individually on <see cref="MeasurementOptions" />.
/// </summary>
public enum MeasurementProfile
{
    /// <summary>
    ///     The default. No Gen0 GC is forced between iterations and no full GC runs between warmup
    ///     and measurement, so the warmup heap is inherited and natural GC pauses are included in
    ///     the timing. Numbers reflect what the same code does in production, including GC pressure
    ///     and CPU cache effects.
    /// </summary>
    Realistic = 0,

    /// <summary>
    ///     A Gen0 GC is forced before every measured iteration and a full GC runs once between
    ///     warmup and measurement (clearing the warmup heap so it cannot trigger a collection
    ///     mid-measurement). Iteration-to-iteration independence is preserved at the cost of
    ///     suppressing the natural GC pressure the code would experience in production. Use this for
    ///     pure-CPU, cryptographic, or numeric benchmarks where execution time matters more than
    ///     ecological validity.
    /// </summary>
    Independent = 1,
}
