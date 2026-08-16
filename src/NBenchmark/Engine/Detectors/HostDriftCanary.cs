namespace NBenchmark.Engine.Detectors;

/// <summary>
///     The host drift canary. Runs <see cref="JitterCalibrator" />'s deterministic busy-weight
///     workload at each benchmark boundary and keeps the readings, so the run can say how much the
///     host's effective speed moved while it was running.
/// </summary>
/// <remarks>
///     <para>
///         The workload is not new. The jitter probe already runs a fixed, allocation-free,
///         self-warming multiply-accumulate loop and computes the median of its per-sample timings
///         - and used to throw that median away, returning only the MAD/median spread. That
///         discarded median is exactly the throughput figure a canary needs, so the canary is the
///         same probe read for its centre instead of its spread.
///     </para>
///     <para>
///         Readings bracket benchmarks rather than interleaving with them: one before the first
///         benchmark, one at each boundary (after the inter-benchmark GC, so a collection the
///         previous benchmark provoked is not charged to the canary), and one after the last. So
///         <c>n</c> benchmarks produce <c>n + 1</c> readings and benchmark <c>i</c> is bracketed by
///         readings <c>i</c> and <c>i + 1</c>.
///     </para>
///     <para>
///         Nothing here is compared in absolute terms. A reading is nanoseconds of an arbitrary
///         amount of work on an arbitrary machine; only the ratio between two readings <em>in the
///         same process</em> means anything, which is why <see cref="HostTimeline.RelativeToRunStart" />
///         normalises against the run's first reading rather than reporting a speed.
///     </para>
/// </remarks>
internal sealed class HostDriftCanary
{
    private readonly IClock _clock;
    private readonly DriftCanaryOptions _options;
    private readonly List<double> _readings = [];

    private HostDriftCanary(DriftCanaryOptions options, IClock clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>How many readings have been taken so far.</summary>
    public int ReadingCount => _readings.Count;

    /// <summary>
    ///     A canary for <paramref name="options" />, or <c>null</c> when the canary is switched
    ///     off. Returning null rather than a no-op instance keeps the "is it on?" question at one
    ///     place in the caller instead of at every reading.
    /// </summary>
    public static HostDriftCanary? Create(DriftCanaryOptions? options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return options is { Enabled: true } enabled ? new HostDriftCanary(enabled, clock) : null;
    }

    /// <summary>
    ///     Takes one reading and appends it to the timeline. A probe that could not produce a
    ///     usable median records <see cref="double.NaN" />, so the reading indices stay aligned
    ///     with the benchmark indices and only the stamps that actually needed the bad reading are
    ///     lost.
    /// </summary>
    public void Take()
    {
        var probe = JitterCalibrator.Run(_options.Samples, _options.WorkPerSample, _clock);

        _readings.Add(probe.HasMedian ? probe.MedianNs : double.NaN);
    }

    /// <summary>
    ///     The stamp for the benchmark at <paramref name="index" /> - the one bracketed by
    ///     readings <paramref name="index" /> and <paramref name="index" /> + 1. Returns
    ///     <c>null</c> when either bracketing reading is missing or unusable, or when the run's
    ///     first reading (the normalisation base) is unusable, because a stamp that cannot be
    ///     compared against the other rows is worse than no stamp: it would render as data while
    ///     meaning nothing.
    /// </summary>
    public HostTimeline? StampFor(int index)
    {
        if (index < 0 || index + 1 >= _readings.Count)
            return null;

        var before = _readings[index];
        var after = _readings[index + 1];
        var origin = _readings[0];

        if (!IsUsable(before) || !IsUsable(after) || !IsUsable(origin))
            return null;

        return new HostTimeline
        {
            BeforeNs = before,
            AfterNs = after,
            RelativeToRunStart = (before + after) / 2.0 / origin,
            Position = index,
        };
    }

    private static bool IsUsable(double reading) => double.IsFinite(reading) && reading > 0;
}
