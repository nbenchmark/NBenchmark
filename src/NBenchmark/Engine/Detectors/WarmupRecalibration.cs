using System.Numerics;

namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Pure post-warmup ops-per-sample recalibration. Ops-per-sample calibration (Phase A) resolves
///     <c>K</c> against the body's <em>cold</em>, pre-warmup speed; after warmup the body runs its
///     steady-state (tiered / PGO-optimized) code, often several times faster, so the same <c>K</c>
///     now spans well under the target sample duration and fixed timer overhead creeps back in.
///     This re-derives <c>K</c> from the warm per-op estimate the plateau detector measured.
///     Separated from the loop so the trigger and the pow-2 rounding are unit-testable in isolation.
/// </summary>
internal static class WarmupRecalibration
{
    /// <summary>
    ///     Returns the recalibrated ops-per-sample, or <paramref name="currentK" /> unchanged when no
    ///     recalibration is warranted. <c>K</c> is only ever increased.
    /// </summary>
    /// <param name="currentK">The ops-per-sample resolved by cold calibration.</param>
    /// <param name="warmPerOpNs">The warm per-op estimate (e.g. the last warmup batch mean).</param>
    /// <param name="targetSampleNs">The target single-sample duration.</param>
    /// <param name="maxOps">The ops-per-sample ceiling.</param>
    /// <param name="triggerFraction">
    ///     Recalibrate only when the current warm sample spans less than this fraction of the target
    ///     (e.g. 0.5 - "under half the target"). Guards against re-tuning a body that is already
    ///     close to the target and against churn from small warm/cold differences.
    /// </param>
    public static int Resolve(
        int currentK,
        double warmPerOpNs,
        double targetSampleNs,
        int maxOps,
        double triggerFraction)
    {
        if (currentK < 1 || warmPerOpNs <= 0 || !double.IsFinite(warmPerOpNs) || targetSampleNs <= 0)
            return currentK;

        // Only recalibrate when the warm sample is well under the target - i.e. calibration (done
        // against cold code) left K too small now that the body runs its warm/optimized path.
        if (warmPerOpNs * currentK >= targetSampleNs * triggerFraction)
            return currentK;

        var neededOps = (int)Math.Min(int.MaxValue, Math.Ceiling(targetSampleNs / warmPerOpNs));
        var newK = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, neededOps));

        // RoundUpToPowerOf2(0) returns 0 for inputs above 2^31; clamp defensively, then to maxOps.
        if (newK < 1)
            newK = maxOps;

        newK = Math.Min(newK, Math.Max(1, maxOps));

        // Never decrease K (the trigger guard already implies an increase, but be explicit).
        return Math.Max(newK, currentK);
    }
}
