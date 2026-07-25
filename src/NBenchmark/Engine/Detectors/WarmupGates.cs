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
    ///     active - the JIT compiled-method count has been unchanged for at least the quiet period.
    ///     Returns <c>false</c> to keep warming up.
    /// </summary>
    /// <param name="warmupElapsedNs">Accumulated in-body warmup nanoseconds so far.</param>
    /// <param name="minWarmupTimeNs">The warmup time floor; <c>0</c> disables the floor (and the JIT gate).</param>
    /// <param name="lastJitChangeAtNs">
    ///     The <paramref name="warmupElapsedNs" /> position at which the JIT compiled-method count was
    ///     last observed to change, or <c>0</c> when it has never changed - so a body that triggers no
    ///     compilation at all counts as quiet from the start and the gate collapses to the time floor.
    /// </param>
    /// <param name="requireJitQuiescence">Whether the JIT-quiescence gate is enabled.</param>
    /// <param name="jitQuietPeriodNs">
    ///     How long the compiled-method count must stay unchanged before the gate opens. <c>0</c>
    ///     disables the gate. Expected to have been clamped to <paramref name="minWarmupTimeNs" /> by
    ///     the caller, so the gate can never become the binding floor.
    /// </param>
    /// <param name="jitGateDeactivateNs">
    ///     Warmup-ns threshold past which the JIT gate is ignored so unrelated background JIT on a
    ///     busy in-process host cannot block warmup forever. Typically 4 × <paramref name="minWarmupTimeNs" />.
    /// </param>
    public static bool CanSettle(
        double warmupElapsedNs,
        double minWarmupTimeNs,
        double lastJitChangeAtNs,
        bool requireJitQuiescence,
        double jitQuietPeriodNs,
        double jitGateDeactivateNs)
    {
        // Time floor: never settle before the body has warmed for at least MinWarmupTime. This is what
        // gives background tiered compilation (tier-0 -> tier-1 -> dynamic PGO) the time to land before
        // measurement starts, rather than plateauing on tier-0 in microseconds.
        if (warmupElapsedNs < minWarmupTimeNs)
            return false;

        // Gate off: no floor means there is no timescale to measure a quiet period against, and a zero
        // quiet period means the user asked for the time floor alone.
        if (!requireJitQuiescence || minWarmupTimeNs <= 0 || jitQuietPeriodNs <= 0)
            return true;

        // Escape hatch: past the deactivation threshold the gate stops blocking, so a busy host that
        // JITs unrelated code cannot hold warmup open indefinitely. The calibration+warmup budget
        // share is the ultimate bound beyond this.
        if (warmupElapsedNs >= jitGateDeactivateNs)
            return true;

        // JIT-quiescence gate: require a *sustained* quiet interval, not merely a quiet batch. Asking
        // whether the JIT compiled anything during the most recent batch cannot work - for a fast body
        // one batch spans tens of microseconds, so a background tier-1 compilation almost never lands
        // inside that specific window and the per-batch delta reads zero essentially always. Measuring
        // how long it has been since the count last moved puts the window on the same timescale as the
        // phenomenon, so an in-flight promotion actually extends warmup.
        return warmupElapsedNs - lastJitChangeAtNs >= jitQuietPeriodNs;
    }
}
