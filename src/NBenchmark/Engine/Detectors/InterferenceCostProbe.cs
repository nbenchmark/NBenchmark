using NBenchmark.Interop;

namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Measures the wall-clock cost of the pair of <see cref="ThreadCpuClock.TryRead" /> calls the
///     adaptive loop brackets every timed sample with, once per process - the guard that makes
///     bracketing a sample with a thread-CPU-clock read safe to default on even though the macOS
///     Mach-trap cost is not advertised anywhere and has to be measured rather than assumed.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors <c>ClockResolutionProbe</c>'s once-per-process <see cref="Lazy{T}" />: the probe's
///         cost is a property of the host and the platform's syscall implementation, not of any one
///         benchmark, so it is measured once and reused for every benchmark in the process.
///     </para>
///     <para>
///         The minimum across several attempts is used, exactly as <c>ClockResolutionProbe</c>
///         reasons about its own measurement: an attempt inflated by a one-off scheduling preemption
///         would overstate the probe's true cost and could needlessly disable the feature.
///     </para>
/// </remarks>
internal static class InterferenceCostProbe
{
    /// <summary>Attempts to time. Small: each attempt is itself only two clock reads.</summary>
    private const int DefaultAttempts = 16;

    private static readonly Lazy<double> CachedPairCostNs =
        new(() => Measure(StopwatchClock.WallClock), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     The measured wall-clock cost, in nanoseconds, of reading <see cref="ThreadCpuClock" />
    ///     twice back-to-back - the shape every timed sample brackets. <c>0</c> when
    ///     <see cref="ThreadCpuClock.IsAvailable" /> is <c>false</c>, since there is nothing to cost.
    /// </summary>
    public static double PairCostNs => ThreadCpuClock.IsAvailable ? CachedPairCostNs.Value : 0.0;

    /// <summary>
    ///     Times <paramref name="attempts" /> pairs of back-to-back <see cref="ThreadCpuClock.TryRead" />
    ///     calls using <paramref name="clock" /> and returns the smallest elapsed reading. Exposed
    ///     (rather than only the cached property) so a test can drive it against a fake clock without
    ///     waiting for the real syscall - the same seam <c>ClockResolutionProbe.Measure</c> offers.
    /// </summary>
    internal static double Measure(IClock clock, int attempts = DefaultAttempts)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!ThreadCpuClock.IsAvailable)
            return 0.0;

        var best = double.PositiveInfinity;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            var start = clock.GetTimestamp();
            ThreadCpuClock.TryRead(out _);
            ThreadCpuClock.TryRead(out _);
            var elapsed = clock.GetElapsedNanoseconds(start);

            if (double.IsFinite(elapsed) && elapsed >= 0 && elapsed < best)
                best = elapsed;
        }

        return double.IsFinite(best) ? best : 0.0;
    }
}
