namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Pure settle-gate logic for auto-warmup, separated from <see cref="WarmupPlateauDetector" />
///     so the decision table is unit-testable with injected values (no clock or JIT-counter reads).
///     The plateau rule decides <em>whether the body has stopped getting faster</em>; these gates
///     decide <em>whether it is safe to stop warming up</em> even once the plateau is reached.
/// </summary>
internal static class WarmupGates
{
    /// <summary>
    ///     Returns <c>true</c> when auto-warmup may settle (the plateau rule having already fired):
    ///     the accumulated warmup time has reached the floor, and - while the JIT-quiescence gate is
    ///     active - the JIT compiled nothing during the most recent batch. Returns <c>false</c> to
    ///     keep warming up.
    /// </summary>
    /// <param name="warmupElapsedNs">Accumulated in-body warmup nanoseconds so far.</param>
    /// <param name="minWarmupTimeNs">The warmup time floor; <c>0</c> disables the floor (and the JIT gate).</param>
    /// <param name="jitCompiledDeltaLastBatch">Methods the JIT compiled during the most recent batch.</param>
    /// <param name="requireJitQuiescence">Whether the JIT-quiescence gate is enabled.</param>
    /// <param name="jitGateDeactivateNs">
    ///     Warmup-ns threshold past which the JIT gate is ignored so unrelated background JIT on a
    ///     busy in-process host cannot block warmup forever. Typically 4 × <paramref name="minWarmupTimeNs" />.
    /// </param>
    public static bool CanSettle(
        double warmupElapsedNs,
        double minWarmupTimeNs,
        long jitCompiledDeltaLastBatch,
        bool requireJitQuiescence,
        double jitGateDeactivateNs)
    {
        // Time floor: never settle before the body has warmed for at least MinWarmupTime. This is
        // what gives background tiered compilation (tier-0 -> tier-1 -> dynamic PGO) the wall-clock
        // time to land before measurement starts, rather than plateauing on tier-0 in microseconds.
        if (warmupElapsedNs < minWarmupTimeNs)
            return false;

        // JIT-quiescence gate: while active, refuse to settle if the JIT compiled anything during
        // the last batch (tier-1 promotion of the body is likely still in flight). The gate
        // deactivates past jitGateDeactivateNs so a busy host that JITs unrelated code cannot block
        // warmup indefinitely.
        if (requireJitQuiescence
            && warmupElapsedNs < jitGateDeactivateNs
            && jitCompiledDeltaLastBatch > 0)
        {
            return false;
        }

        return true;
    }
}
