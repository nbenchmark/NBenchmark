namespace NBenchmark;

/// <summary>
///     A machine-readable record of what the adaptive measurement loop decided for one
///     benchmark and why. Present on every measured <see cref="BenchmarkResult" /> (including
///     fully pinned runs, where the stop reasons are <c>ExplicitCount</c>); <c>null</c> on
///     dry-run and errored results, where nothing was measured.
/// </summary>
public sealed record AutoTuneDiagnostic
{
    /// <summary>The number of warmup samples that were discarded before measurement.</summary>
    public required int ResolvedWarmup { get; init; }

    /// <summary>The number of measured samples collected, before outlier trimming.</summary>
    public required int ResolvedSamples { get; init; }

    /// <summary>The ops-per-sample count (<c>K</c>): how many back-to-back body invocations made up one timed sample.</summary>
    public required int OpsPerSample { get; init; }

    /// <summary>
    ///     The total number of body invocations across every phase of the loop &#8212; ops-per-sample
    ///     calibration, warmup, and measurement &#8212; counting each timed and untimed sample at the
    ///     ops-per-sample count in effect when it ran.
    /// </summary>
    public required long TotalBodyInvocations { get; init; }

    /// <summary>Why the warmup phase stopped.</summary>
    public required WarmupStopReason WarmupStop { get; init; }

    /// <summary>Why the measurement phase stopped.</summary>
    public required SampleStopReason SampleStop { get; init; }

    /// <summary>
    ///     The relative confidence-interval half-width achieved at stop, computed on the raw
    ///     (untrimmed) measured stream. The reported interval on <see cref="BenchmarkResult" /> is
    ///     computed on the trimmed samples and may differ slightly.
    /// </summary>
    public required double AchievedRelativeCiWidth { get; init; }

    /// <summary>
    ///     The wall-clock time spent in the adaptive loop itself (calibration, warmup, and
    ///     measurement) for this benchmark, excluding the runner's surrounding setup and progress
    ///     callbacks.
    /// </summary>
    public required TimeSpan TuningWallClock { get; init; }
}
