using System.Globalization;

namespace NBenchmark.Stats;

/// <summary>
///     Compares two benchmarks' <see cref="HostTimeline" /> stamps and says whether the difference
///     between them is small enough that the host's own drift could account for it.
/// </summary>
/// <remarks>
///     <para>
///         This is the whole reason the drift canary exists. Every other drift check in NBenchmark
///         works inside one benchmark's sample stream, so a machine that got 6% slower between the
///         baseline and the candidate produces two internally consistent, tightly-intervalled
///         results whose 5% difference is the room warming up. Nothing in the run could previously
///         say so.
///     </para>
///     <para>
///         It warns and never downgrades. <c>MinimumPracticalEffect</c> and
///         <c>MinimumRelativeShift</c> downgrade a verdict because they are statements about the
///         comparison itself - the effect really is negligible, the shift really is sub-percent.
///         The canary is a statement about the <em>machine</em>, measured by a different workload
///         at a different moment, so it is indirect evidence about the comparison and belongs in
///         the reader's hands rather than in the verdict.
///     </para>
/// </remarks>
internal static class HostDrift
{
    /// <summary>
    ///     How far the host's effective speed moved between the points at which
    ///     <paramref name="candidate" /> and <paramref name="baseline" /> were measured, as a
    ///     fraction of the faster of the two. <c>null</c> when either row has no stamp.
    /// </summary>
    public static double? Between(HostTimeline? candidate, HostTimeline? baseline)
    {
        if (candidate is null || baseline is null)
            return null;

        var a = candidate.RelativeToRunStart;
        var b = baseline.RelativeToRunStart;

        if (!double.IsFinite(a) || !double.IsFinite(b) || a <= 0 || b <= 0)
            return null;

        return Math.Abs(a - b) / Math.Min(a, b);
    }

    /// <summary>
    ///     The warning for one candidate-versus-baseline comparison, or <c>null</c> when the drift
    ///     between the two measurement points is below <paramref name="minimumReportableDrift" />
    ///     or smaller than the difference being reported.
    /// </summary>
    /// <param name="candidate">The row being compared.</param>
    /// <param name="baseline">The row it is compared against.</param>
    /// <param name="relativeShift">
    ///     The reported difference, as a fraction of the baseline median. Absolute value is taken,
    ///     so the caller may pass a signed shift.
    /// </param>
    /// <param name="minimumReportableDrift">
    ///     <see cref="DriftCanaryOptions.MinimumReportableDrift" /> - the floor below which canary
    ///     movement is the canary's own noise rather than the host's speed.
    /// </param>
    public static string? Describe(
        BenchmarkResult candidate,
        BenchmarkResult baseline,
        double relativeShift,
        double minimumReportableDrift)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        if (Between(candidate.HostTimeline, baseline.HostTimeline) is not { } drift)
            return null;

        if (drift < minimumReportableDrift)
            return null;

        var shift = Math.Abs(relativeShift);

        if (!double.IsFinite(shift) || shift >= drift)
            return null;

        // Which way the host moved, so the reader knows whether the drift flatters this row or
        // penalises it. The canary times fixed work, so a larger reading is a slower host.
        var direction = candidate.HostTimeline!.RelativeToRunStart > baseline.HostTimeline!.RelativeToRunStart
            ? "slower"
            : "faster";

        return string.Format(
            CultureInfo.InvariantCulture,
            "host drift exceeds the difference being reported: the machine was {0:0.##%} {1} when "
            + "'{2}' was measured than when '{3}' was, and the {4:0.##%} difference between them is "
            + "smaller than that. Treat the comparison as unresolved rather than as a result - "
            + "re-run the two benchmarks with --order declaration on a quiet host, raise "
            + "--launch-count so the two are measured co-resident several times, or pin the host "
            + "with --cpu-affinity. Set --no-drift-canary to stop checking.",
            drift,
            direction,
            candidate.Name,
            baseline.Name,
            shift);
    }
}
