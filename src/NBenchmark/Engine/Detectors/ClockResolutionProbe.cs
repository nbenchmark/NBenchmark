namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Measures the <em>effective resolution</em> of the measurement clock: the smallest non-zero
///     interval the engine can actually observe through <see cref="IClock.GetElapsedNanoseconds" />.
///     <para>
///         This is deliberately not read from <c>Stopwatch.Frequency</c>, which is an advertised rate
///         rather than an observed one and can be wrong by more than an order of magnitude. On
///         Apple Silicon it reports 1 GHz - implying 1 ns granularity - while the underlying
///         <c>mach_absolute_time</c> timebase runs at 24 MHz, so the counter only ever advances in
///         steps of 41.667 ns. A sample at the default 10 µs target spans about 250 of those steps, which
///         puts a single step at 0.4% of the measurement: an order of magnitude larger than the error
///         margin such a run typically reports, and the same order as the whole CI target. Windows QPC
///         (10 MHz, 100 ns) has the same problem more severely; a TSC-backed Linux host has it barely
///         at all. A hard-coded sample-duration target therefore means something quite different on
///         each host, which is what this probe exists to fix.
///     </para>
///     <para>
///         "Effective" rather than "hardware quantum" is the honest label. When reading the clock costs
///         more than one hardware tick - routine, since a counter read is tens of nanoseconds - the
///         smallest observable delta is bounded by the read itself, not by the timebase. That bound is
///         the right number for the engine's purposes: it is the finest interval a timed sample can
///         actually distinguish, whatever sets it.
///     </para>
/// </summary>
internal static class ClockResolutionProbe
{
    /// <summary>
    ///     Attempts to spin for. Each attempt costs roughly one resolution step (tens of nanoseconds),
    ///     so the whole probe is a microsecond or two. The minimum across attempts discards attempts
    ///     that were preempted or that straddled two steps.
    /// </summary>
    internal const int DefaultAttempts = 32;

    /// <summary>
    ///     Clock reads per attempt before giving up. Generous: a resolution step is normally observed
    ///     within a handful of reads, and the bound exists only so a pathological or stopped clock
    ///     cannot hang the probe.
    /// </summary>
    private const int MaxReadsPerAttempt = 1024;

    /// <summary>
    ///     The wall-clock resolution, measured once per process. The clock is a property of the host,
    ///     not of any one benchmark, so every benchmark after the first reads the cached value.
    ///     <see cref="Lazy{T}" /> rather than a null check because several workers' benchmarks can share
    ///     a process and the probe must not race.
    /// </summary>
    private static readonly Lazy<double> CachedWallClockResolution =
        new(() => Measure(StopwatchClock.WallClock), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     The effective resolution in nanoseconds of the real wall clock, or <c>0</c> when
    ///     <paramref name="clock" /> is not a real-time clock or the probe could not determine a value.
    ///     Callers treat <c>0</c> as "unknown" and skip every resolution-derived adjustment.
    ///     <para>
    ///         Only a real-time clock is probed, and the result is measured once per process and cached -
    ///         the clock is a property of the host, not of any one benchmark. An injected clock is
    ///         reported as unknown rather than measured: probing calls
    ///         <see cref="IClock.GetTimestamp" /> in a loop, and a fake generally serves a finite
    ///         scripted sequence, so probing it would consume the readings the test scheduled for the
    ///         measurement. Its "resolution" would in any case be an artefact of the script.
    ///     </para>
    ///     <para>
    ///         Use <see cref="Measure(IClock, int)" /> directly to exercise the probe against a stub in
    ///         a unit test.
    ///     </para>
    /// </summary>
    public static double ResolutionNs(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return clock is StopwatchClock { IsRealTime: true }
            ? CachedWallClockResolution.Value
            : 0.0;
    }

    /// <summary>
    ///     Spins on the clock until the reported elapsed time first becomes non-zero, and returns the
    ///     smallest such reading across <paramref name="attempts" /> attempts. Returns <c>0</c> when no
    ///     attempt ever saw the clock advance.
    /// </summary>
    internal static double Measure(IClock clock, int attempts = DefaultAttempts)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var best = double.PositiveInfinity;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            var start = clock.GetTimestamp();
            var elapsed = 0.0;

            for (var read = 0; read < MaxReadsPerAttempt; read++)
            {
                elapsed = clock.GetElapsedNanoseconds(start);

                // A non-finite or negative reading means the clock is unusable for this purpose;
                // abandon the attempt rather than feed a garbage value into the minimum.
                if (!double.IsFinite(elapsed) || elapsed < 0)
                {
                    elapsed = 0;
                    break;
                }

                if (elapsed > 0)
                    break;
            }

            if (elapsed > 0 && elapsed < best)
                best = elapsed;
        }

        return double.IsFinite(best) ? best : 0.0;
    }

    /// <summary>
    ///     Raises <paramref name="configuredTargetNs" /> so a single timed sample spans at least
    ///     <paramref name="minQuanta" /> resolution steps, keeping clock granularity to a small
    ///     fraction of the measurement. Returns the configured target unchanged when the resolution is
    ///     unknown, when the floor is disabled, or when the configured target already clears it - the
    ///     target is only ever raised, never lowered.
    /// </summary>
    /// <param name="configuredTargetNs">The configured <see cref="AutoTuneOptions.TargetSampleDurationNs" />.</param>
    /// <param name="resolutionNs">The measured effective resolution, or <c>0</c> for unknown.</param>
    /// <param name="minQuanta">
    ///     Resolution steps a sample must span. <c>0</c> or less disables the floor, leaving the
    ///     configured target authoritative on every host.
    /// </param>
    public static double ResolveTargetSampleDurationNs(double configuredTargetNs, double resolutionNs, int minQuanta)
    {
        if (minQuanta <= 0 || resolutionNs <= 0 || !double.IsFinite(resolutionNs))
            return configuredTargetNs;

        var floor = resolutionNs * minQuanta;

        return double.IsFinite(floor) && floor > configuredTargetNs ? floor : configuredTargetNs;
    }

    /// <summary>
    ///     The fraction of one timed sample that a single resolution step represents - the granularity
    ///     floor on how finely that sample can be resolved. Returns <c>0</c> when either input is
    ///     unusable.
    ///     <para>
    ///         This is a floor on <em>reproducibility</em>, not on within-run spread, and the
    ///         distinction is what makes it worth reporting. Within a run, consecutive samples of a
    ///         stable body land on the same step and the reported margin collapses toward zero; between
    ///         runs, a shift far smaller than one step is enough to move every sample to the next step
    ///         and the median with it. A margin well below this fraction is describing the step grid
    ///         rather than the code.
    ///     </para>
    /// </summary>
    public static double QuantizationFraction(double resolutionNs, double sampleDurationNs)
    {
        if (resolutionNs <= 0 || sampleDurationNs <= 0
            || !double.IsFinite(resolutionNs) || !double.IsFinite(sampleDurationNs))
            return 0.0;

        return resolutionNs / sampleDurationNs;
    }
}
