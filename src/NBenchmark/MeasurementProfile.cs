namespace NBenchmark;

/// <summary>
///     Controls how garbage collection interacts with the measurement loop. The profile is a
///     bundle that drives three behaviours: per-iteration Gen0 GC, between-benchmark full GC, and
///     allocation tracking. Each behaviour can be overridden individually on <see cref="MeasurementOptions" />.
/// </summary>
public enum MeasurementProfile
{
    /// <summary>
    ///     The default. No Gen0 GC is forced between iterations and no full GC runs between
    ///     benchmarks, so natural GC pauses are included in the timing. Allocation tracking is on.
    ///     Numbers reflect what the same code does in production, including GC pressure and CPU
    ///     cache effects.
    /// </summary>
    Realistic = 0,

    /// <summary>
    ///     A Gen0 GC is forced before every measured iteration and a full GC runs between
    ///     benchmarks. Iteration-to-iteration independence is preserved at the cost of suppressing
    ///     the natural GC pressure the code would experience in production. Allocation tracking is
    ///     off. Use this for pure-CPU, cryptographic, or numeric benchmarks where execution time
    ///     matters more than ecological validity.
    /// </summary>
    Independent = 1,
}
