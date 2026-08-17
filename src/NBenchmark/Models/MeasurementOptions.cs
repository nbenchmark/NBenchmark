using System.Text.Json.Serialization;
using NBenchmark.Stats;

namespace NBenchmark;

/// <summary>
///     How <em>one</em> measurement is taken.
/// </summary>
/// <remarks>
///     Every field here is consumed by the process doing the measuring, and this record is serialized
///     whole into each worker's request - so a field that only a coordinator could act on has no
///     business on it. That is why the replicate count is not here but in
///     <see cref="LaunchCounts" />, which explains the reasoning.
/// </remarks>
public record MeasurementOptions
{
    internal const double PercentileEqualityTolerance = 1e-9;
    public const int MinIterations = 0;
    public const int MaxIterations = 100_000;

    /// <summary>The ceiling on a <em>pinned</em> <see cref="WarmupIterations" /> (and <c>--warmup</c>).</summary>
    public const int MaxWarmupIterations = 10_000;

    /// <summary>
    ///     The ceiling on <em>auto</em>-resolved warmup (<see cref="AutoTuneOptions.MaxWarmup" /> and
    ///     <c>--max-warmup</c> / <c>--min-warmup</c>). Deliberately far above
    ///     <see cref="MaxWarmupIterations" />: a fast body needs tens of thousands of samples to reach
    ///     <see cref="AutoTuneOptions.MinWarmupTime" />, and a count ceiling that binds first would
    ///     silently defeat that floor.
    /// </summary>
    public const int MaxAutoWarmupIterations = 100_000;

    public const int MaxOpsPerSampleLimit = 1 << 24;
    public const int MinHistogramBucketCount = 5;
    public const int MaxHistogramBucketCount = 100;

    /// <summary>
    ///     Default ceiling on how many raw samples an isolated worker sends back per benchmark.
    ///     See <see cref="MaxRawSamples" />.
    /// </summary>
    public const int DefaultMaxRawSamples = 4096;

    /// <summary><see cref="MaxRawSamples" /> value meaning "return every sample".</summary>
    public const int UnboundedRawSamples = 0;

    /// <summary>
    ///     Default ceiling on the encoded size of the values a benchmark's closure may send to a
    ///     measurement worker. See <see cref="MaxTransferredStateBytes" />.
    /// </summary>
    public const int DefaultMaxTransferredStateBytes = 8 * 1024 * 1024;

    internal static readonly IReadOnlyList<double> DefaultReportedPercentiles =
        Array.AsReadOnly(new[] { 0.50, 0.95, 0.99, 0.999, 1.0 });

    public static readonly MeasurementOptions Default = new();
    private readonly double _confidenceLevel = 0.95;
    private readonly int _histogramBucketCount = 20;
    private readonly int _maxRawSamples = DefaultMaxRawSamples;
    private readonly int? _iterations;
    private readonly double? _minimumPracticalEffect = DefaultMinimumPracticalEffect;
    private readonly double? _minimumRelativeShift = DefaultMinimumRelativeShift;

    /// <summary>
    ///     The default <see cref="MinimumPracticalEffect" />: 0.147, the Romano boundary between a
    ///     negligible and a small effect (the same threshold the Magnitude column uses). A ✓ verdict
    ///     therefore means "statistically real <em>and</em> at least a small effect". Set the option
    ///     to <c>0</c> to restore p-value-only verdicts.
    /// </summary>
    public const double DefaultMinimumPracticalEffect = 0.147;

    /// <summary>
    ///     The default <see cref="MinimumRelativeShift" />: 0.01 (1%). Conservative - it kills the
    ///     false positive of a sub-percent shift measured with near-zero spread (which the U test
    ///     rejects and Cliff's delta scores as a large effect) without being opinionated about what
    ///     a real effect size is. Set to <c>0</c> to restore practical-effect-only gating.
    /// </summary>
    public const double DefaultMinimumRelativeShift = 0.01;
    private readonly int? _opsPerSample;
    private readonly IReadOnlyList<double> _reportedPercentiles = DefaultReportedPercentiles;
    private readonly double _significanceLevel = 0.05;
    private readonly int? _warmupIterations;

    /// <summary>
    ///     The number of warmup samples to discard before measurement. <c>null</c> (the default)
    ///     auto-detects warmup length with a plateau rule; <c>0</c> skips warmup; a positive value
    ///     pins an exact count. Must be between 0 and <see cref="MaxWarmupIterations" /> when set.
    /// </summary>
    public int? WarmupIterations
    {
        get => _warmupIterations;
        init
        {
            if (value is { } count && count is < 0 or > MaxWarmupIterations)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"WarmupIterations must be null (auto) or between 0 and {MaxWarmupIterations}.");
            }

            _warmupIterations = value;
        }
    }

    /// <summary>
    ///     The number of measured samples to collect. <c>null</c> (the default) auto-detects the
    ///     count from a confidence-interval-width target; <c>0</c> is a dry-run; a positive value
    ///     pins an exact count. Must be between 0 and <see cref="MaxIterations" /> when set.
    /// </summary>
    public int? Iterations
    {
        get => _iterations;
        init
        {
            if (value is { } count && count is < 0 or > MaxIterations)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Iterations must be null (auto) or between 0 and {MaxIterations} (0 = dry-run).");
            }

            _iterations = value;
        }
    }

    /// <summary>
    ///     The number of back-to-back body invocations timed as one sample (<c>K</c>). <c>null</c>
    ///     (the default) auto-calibrates <c>K</c> so a sample spans roughly
    ///     <see cref="AutoTuneOptions.TargetSampleDurationNs" />, amortising timer overhead on fast
    ///     bodies; a value of <c>1</c> or more pins <c>K</c> (always honoured, even with
    ///     per-iteration setup/teardown). Must be between 1 and <see cref="MaxOpsPerSampleLimit" />
    ///     when set.
    /// </summary>
    public int? OpsPerSample
    {
        get => _opsPerSample;
        init
        {
            if (value is { } count && count is < 1 or > MaxOpsPerSampleLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"OpsPerSample must be null (auto) or between 1 and {MaxOpsPerSampleLimit}.");
            }

            _opsPerSample = value;
        }
    }

    /// <summary>
    ///     Tuning knobs for the adaptive measurement loop (warmup plateau, CI-width sample count,
    ///     and ops-per-sample calibration). Defaults to <see cref="AutoTuneOptions.Default" />.
    /// </summary>
    public AutoTuneOptions AutoTune { get; init; } = AutoTuneOptions.Default;

    /// <summary>
    ///     Diagnostics options controlling which runtime counters are collected during measurement.
    ///     GC collection counts are on by default (cheap, always available); heap info, exceptions,
    ///     and CPU time are opt-in. Defaults to <see cref="DiagnosticsOptions.Default" />.
    /// </summary>
    public DiagnosticsOptions Diagnostics { get; init; } = DiagnosticsOptions.Default;

    /// <summary>
    ///     Settings for the host drift canary - the deterministic control workload measured at each
    ///     benchmark boundary, which is what lets a run say how much the host's effective speed
    ///     moved while it was running. On by default; see <see cref="DriftCanaryOptions" />.
    /// </summary>
    public DriftCanaryOptions DriftCanary { get; init; } = DriftCanaryOptions.Default;

    /// <summary>
    ///     Settings for evidence-based interference rejection - discarding samples the OS is known
    ///     to have preempted (via the measuring thread's own CPU-occupancy ratio) before the
    ///     statistical outlier detector ever sees the stream. On by default; see
    ///     <see cref="InterferenceOptions" />.
    /// </summary>
    public InterferenceOptions Interference { get; init; } = InterferenceOptions.Default;

    /// <summary>
    ///     The authoritative measurement profile. The resolved booleans
    ///     (<see cref="ForceGcBeforeEachIteration" />, <see cref="ForceGcBeforeMeasurement" />,
    ///     <see cref="ForceGcBetweenBenchmarks" />, <see cref="MeasureAllocations" />) derive from
    ///     this unless an explicit override is set.
    /// </summary>
    public MeasurementProfile Profile { get; init; } = MeasurementProfile.Realistic;

    /// <summary>Overrides <see cref="ForceGcBeforeEachIteration" />. When <c>null</c>, the value derives from <see cref="Profile" />.</summary>
    public bool? ForceGcBeforeEachIterationOverride { get; init; }

    /// <summary>Overrides <see cref="ForceGcBeforeMeasurement" />. When <c>null</c>, the value derives from <see cref="Profile" />.</summary>
    public bool? ForceGcBeforeMeasurementOverride { get; init; }

    /// <summary>Overrides <see cref="ForceGcBetweenBenchmarks" />. When <c>null</c>, the value defaults to <c>true</c>.</summary>
    public bool? ForceGcBetweenBenchmarksOverride { get; init; }

    /// <summary>Overrides <see cref="MeasureAllocations" />. When <c>null</c>, allocation tracking defaults to <c>true</c>.</summary>
    public bool? MeasureAllocationsOverride { get; init; }

    /// <summary>Whether a Gen0 GC is forced before each measured iteration. Forced under <see cref="MeasurementProfile.Independent" />, unless overridden.</summary>
    public bool ForceGcBeforeEachIteration =>
        ForceGcBeforeEachIterationOverride ?? Profile == MeasurementProfile.Independent;

    /// <summary>
    ///     Whether a full GC runs once between warmup and measurement, clearing the warmup heap so
    ///     it cannot trigger a collection mid-measurement. Forced under
    ///     <see cref="MeasurementProfile.Independent" />, unless overridden;
    ///     <see cref="MeasurementProfile.Realistic" /> deliberately inherits the warmup heap to match
    ///     production. Distinct from <see cref="ForceGcBetweenBenchmarks" />, which runs a full GC
    ///     <em>between</em> benchmarks to keep them independent of one another.
    /// </summary>
    public bool ForceGcBeforeMeasurement =>
        ForceGcBeforeMeasurementOverride ?? Profile == MeasurementProfile.Independent;

    /// <summary>
    ///     Whether a full GC runs between benchmarks so one benchmark's leftover heap cannot bias
    ///     the next (which would make results order-dependent and undermine the significance test's
    ///     independence assumption). On by default under both profiles, unless overridden.
    /// </summary>
    public bool ForceGcBetweenBenchmarks =>
        ForceGcBetweenBenchmarksOverride ?? true;

    /// <summary>Whether per-iteration allocations are sampled and reported. On by default under both profiles, unless overridden.</summary>
    public bool MeasureAllocations =>
        MeasureAllocationsOverride ?? true;

    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>
    ///     Which sample set the order statistics (percentiles, min, max, histogram) are computed
    ///     from. Default <see cref="TailMetricsBasis.Raw" /> - the full pre-trim distribution, so the
    ///     tail metrics describe the tail the outlier fence removed rather than the inliers.
    ///     Central-tendency and dispersion statistics always stay on the trimmed set.
    /// </summary>
    public TailMetricsBasis TailMetricsBasis { get; init; } = TailMetricsBasis.Raw;

    /// <summary>
    ///     A custom outlier-detection strategy. When set, it takes precedence over
    ///     <see cref="OutlierMode" />, letting you plug in your own trimming algorithm.
    ///     Leave <c>null</c> to use the built-in detector selected by <see cref="OutlierMode" />.
    /// </summary>
    /// <remarks>
    ///     Excluded from serialization: a strategy object is live code, not data, so it cannot
    ///     travel to a measurement worker as a value. It travels instead as an assembly-qualified
    ///     type name that the worker instantiates through its own load context, which works for
    ///     any detector with a parameterless constructor. See <c>NBenchmark.Workers</c>.
    /// </remarks>
    [JsonIgnore]
    public IOutlierDetector? OutlierDetector { get; init; }

    /// <summary>
    ///     A factory for <see cref="OutlierDetector" />, which is what lets a detector needing
    ///     constructor arguments be used in an isolated run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Sending a type name works only for a detector with a parameterless constructor. Anything
    ///         configured - <c>new KeepFastestDetector(0.9)</c> - could not be rebuilt in the worker, so
    ///         the whole group was measured in the host process instead, to avoid the far worse outcome
    ///         of scoring it under a different statistical method than the caller chose.
    ///     </para>
    ///     <para>
    ///         A static, non-capturing factory is addressable, so the worker can run it and get the
    ///         caller's own detector with its own arguments. Set alongside
    ///         <see cref="OutlierDetector" /> rather than instead of it: the coordinator still needs a
    ///         live instance for its own scoring, and the factory is only how the worker obtains one.
    ///     </para>
    /// </remarks>
    [JsonIgnore]
    public Func<IOutlierDetector>? OutlierDetectorFactory { get; init; }

    /// <summary>
    ///     Confidence level for the interval reported on the mean (e.g. 0.95 for 95%).
    ///     Must be strictly between 0 and 1.
    /// </summary>
    public double ConfidenceLevel
    {
        get => _confidenceLevel;
        init => _confidenceLevel = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "ConfidenceLevel must be strictly between 0 and 1 (e.g. 0.95).");
    }

    /// <summary>
    ///     The set of percentiles to compute and report (values in [0, 1]).
    ///     Default: [0.50, 0.95, 0.99, 0.999, 1.0] (P50, P95, P99, P99.9, Max).
    ///     Use 1.0 to report the sample maximum for display alongside integer percentiles.
    ///     Values are normalized to ascending order with duplicates removed.
    /// </summary>
    public IReadOnlyList<double> ReportedPercentiles
    {
        get => _reportedPercentiles;
        init => _reportedPercentiles = NormalizePercentiles(value);
    }

    /// <summary>
    ///     Whether to compute a latency histogram from the trimmed samples.
    ///     Enabled by default. Set to <c>false</c> to skip histogram computation
    ///     and keep the <see cref="BenchmarkResult.Histogram" /> property <c>null</c>.
    /// </summary>
    public bool EnableHistogram { get; init; } = true;

    /// <summary>
    ///     The number of buckets in the latency histogram. Only used when
    ///     <see cref="EnableHistogram" /> is <c>true</c>.
    ///     Must be between <see cref="MinHistogramBucketCount" /> and
    ///     <see cref="MaxHistogramBucketCount" />. Default 20.
    /// </summary>
    public int HistogramBucketCount
    {
        get => _histogramBucketCount;
        init => _histogramBucketCount = value is >= MinHistogramBucketCount and <= MaxHistogramBucketCount
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"HistogramBucketCount must be between {MinHistogramBucketCount} and {MaxHistogramBucketCount}.");
    }

    /// <summary>
    ///     The largest value <see cref="MaxTransferredStateBytes" /> may be set to: 32 MiB, half the
    ///     protocol's 64 MiB frame ceiling.
    /// </summary>
    public const int MaxTransferredStateCeiling = 32 * 1024 * 1024;

    private readonly int _maxTransferredStateBytes = DefaultMaxTransferredStateBytes;

    /// <summary>
    ///     Ceiling on the encoded size of the values a benchmark's closure may send to a measurement
    ///     worker. Default <see cref="DefaultMaxTransferredStateBytes" /> (8 MiB).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A lambda that closes over data has that data sent to the process that measures it, so
    ///         the benchmark can be isolated without the value being rebuilt from a guess. Past a
    ///         certain size that trade stops being worth making: the frame ceiling is 64 MiB, and a
    ///         value large enough to approach it is one a prepare delegate would build in the worker
    ///         faster than this can ship it - and more faithfully, since it would then be built by the
    ///         same code in the same process rather than reconstructed.
    ///     </para>
    ///     <para>
    ///         Exceeding it is a refusal naming the prepare delegate, not a truncation. A truncated
    ///         capture would measure a smaller input under the caller's name.
    ///     </para>
    ///     <para>
    ///         Bounded by <see cref="MaxTransferredStateCeiling" />, which is well under the frame
    ///         ceiling this reasons about. Raising it past that point does not buy a larger capture -
    ///         it exchanges a refusal that names the remedy for a frame the transport cannot write,
    ///         and an unwritable frame is a dead group rather than a labelled one. The margin is
    ///         deliberate: the encoded size counted here is the value's own, while the frame also
    ///         carries the rest of the payload and pays for JSON escaping on top.
    ///     </para>
    /// </remarks>
    public int MaxTransferredStateBytes
    {
        get => _maxTransferredStateBytes;
        init => _maxTransferredStateBytes = value is > 0 and <= MaxTransferredStateCeiling
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"MaxTransferredStateBytes must be between 1 and {MaxTransferredStateCeiling}.");
    }

    /// <summary>
    ///     How many raw samples an isolated worker returns per benchmark.
    ///     <see cref="UnboundedRawSamples" /> returns all of them. Default
    ///     <see cref="DefaultMaxRawSamples" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This bounds only what crosses a process boundary. Every statistic NBenchmark reports
    ///         is computed inside the worker over the complete sample array, so raising or lowering
    ///         this cannot move a median, an interval, or an outlier count. What it affects is the
    ///         sample dump in JSON output, the Console density sparkline, and the coordinator-side
    ///         significance test - all distribution properties, which a few thousand samples describe
    ///         as faithfully as a hundred thousand.
    ///     </para>
    ///     <para>
    ///         The subset is drawn uniformly at random from the full array and kept in measurement
    ///         order, seeded from the run's own seed so a repeat of the same configuration ships the
    ///         same samples. It is not a prefix: the first n samples are the part of the run nearest
    ///         to warmup, which is the least representative slice available.
    ///     </para>
    ///     <para>
    ///         In-process runs are unaffected - there is no boundary to cross, so they always hold
    ///         the complete array. A run that mixes the two therefore has more samples on its
    ///         in-process rows, which changes nothing about the numbers but is worth knowing when
    ///         comparing sample dumps.
    ///     </para>
    /// </remarks>
    public int MaxRawSamples
    {
        get => _maxRawSamples;
        init => _maxRawSamples = value >= UnboundedRawSamples
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"MaxRawSamples must be {UnboundedRawSamples} (unbounded) or positive.");
    }

    /// <summary>
    ///     Whether an isolated worker forwards its live per-sample observer stream
    ///     (<see cref="IMeasurementObserver.OnSample" />) back to the coordinator. Off by default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Like <see cref="MaxRawSamples" /> this bounds only what crosses a process boundary and
    ///         cannot move a reported number. It is off by default because it is the one channel whose
    ///         cost scales with how fast the benchmarked code is: a nanosecond body emits thousands of
    ///         sample events, and encoding them puts the cost of observing the run inside the run. Phase
    ///         transitions, detector snapshots and results cross either way - they are emitted a handful
    ///         of times per benchmark.
    ///     </para>
    ///     <para>
    ///         Turn it on for a consumer that needs the samples <i>live</i> - a streaming histogram, a
    ///         sample-level exporter - and accept that the run is being observed more intrusively than
    ///         the default. Nothing needs it to report a result: the complete series arrives with the
    ///         result either way, subject to <see cref="MaxRawSamples" />.
    ///     </para>
    ///     <para>
    ///         In-process runs ignore this: the observer is called directly, so there is no boundary to
    ///         forward across and no cost to opt into.
    ///     </para>
    /// </remarks>
    public bool StreamSamples { get; init; }

    public bool EnableSignificance { get; init; } = true;

    /// <summary>
    ///     A custom statistical significance strategy. When set, it takes precedence over the
    ///     built-in default (Mann-Whitney U for two groups, Kruskal-Wallis for three or
    ///     more), letting you plug in your own comparison. Leave <c>null</c> to use the
    ///     default strategy.
    /// </summary>
    /// <remarks>Excluded from serialization for the reason given on <see cref="OutlierDetector" />.</remarks>
    [JsonIgnore]
    public ISignificanceTest? SignificanceTest { get; init; }

    /// <summary>
    ///     A factory for <see cref="SignificanceTest" />, for the reason given on
    ///     <see cref="OutlierDetectorFactory" />.
    /// </summary>
    [JsonIgnore]
    public Func<ISignificanceTest>? SignificanceTestFactory { get; init; }

    /// <summary>
    ///     The significance level (alpha) a benchmark's p-value must fall below to be
    ///     reported as a statistically significant change versus the baseline. Must be
    ///     strictly between 0 and 1. Default 0.05. Tighten (e.g. 0.001) to gate releases
    ///     on a stricter confidence level.
    /// </summary>
    public double SignificanceLevel
    {
        get => _significanceLevel;
        init => _significanceLevel = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "SignificanceLevel must be strictly between 0 and 1 (e.g. 0.05).");
    }

    /// <summary>
    ///     The minimum practical effect in [0, 1] required for a benchmark to be considered
    ///     meaningfully different. The active significance strategy can map its own effect
    ///     metric to this normalized value via <see cref="EffectSize.PracticalValue" />.
    ///     When the reported practical value is below this threshold, the Sig verdict is
    ///     downgraded to NotSignificant and the magnitude label is forced to <c>neg</c>, and a
    ///     warning records the downgrade.
    ///     Defaults to <see cref="DefaultMinimumPracticalEffect" /> (0.147), so a ✓ means "real
    ///     and at least a small effect". Set to <c>0</c> to restore p-value-only Sig semantics;
    ///     set to <c>null</c> to disable the gate entirely.
    /// </summary>
    public double? MinimumPracticalEffect
    {
        get => _minimumPracticalEffect;
        init
        {
            if (!value.HasValue)
            {
                _minimumPracticalEffect = null;
                return;
            }

            var delta = value.Value;

            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0 || delta > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "MinimumPracticalEffect must be between 0 and 1 inclusive.");
            }

            _minimumPracticalEffect = delta;
        }
    }

    /// <summary>
    ///     The minimum relative median shift (|candidate − baseline| / baseline median) in [0, 1]
    ///     required for a benchmark to be considered meaningfully different, gated <em>in addition
    ///     to</em> <see cref="MinimumPracticalEffect" />. The practical-effect gate catches a
    ///     consistent-but-tiny effect that Cliff's delta still scores as large; this gate catches the
    ///     same case on the shift itself, so a ✓ means "real, at least a small effect, and at least a
    ///     <see cref="DefaultMinimumRelativeShift" /> relative shift". When the relative shift is
    ///     below this threshold the Sig verdict is downgraded to NotSignificant and a warning records
    ///     the downgrade. Defaults to <see cref="DefaultMinimumRelativeShift" /> (0.01); set to
    ///     <c>0</c> to restore practical-effect-only gating, or <c>null</c> to disable the gate.
    /// </summary>
    public double? MinimumRelativeShift
    {
        get => _minimumRelativeShift;
        init
        {
            if (!value.HasValue)
            {
                _minimumRelativeShift = null;
                return;
            }

            var shift = value.Value;

            if (double.IsNaN(shift) || double.IsInfinity(shift) || shift < 0 || shift > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "MinimumRelativeShift must be between 0 and 1 inclusive.");
            }

            _minimumRelativeShift = shift;
        }
    }

    /// <summary>
    ///     When <c>false</c> (the default), a runtime warning is emitted when a class with
    ///     <c>InstanceLifetime.PerClass</c> has more than one <c>[Benchmark]</c> method,
    ///     because shared state across methods violates the statistical-independence
    ///     assumption of the significance test. Set to <c>true</c> to suppress this warning
    ///     when sharing is intentional.
    /// </summary>
    public bool SuppressPerClassIndependenceWarning { get; init; }

    /// <summary>
    ///     Hardware/OS controls applied for the duration of a run: CPU affinity, process
    ///     priority, dedicated-host guidance, and thread-level control. <c>null</c> (the default)
    ///     leaves the process with whatever affinity and priority the host started it with, but
    ///     is <b>not</b> inert: <see cref="EnvironmentOptions.ThreadControl" /> defaults to on, so
    ///     the measuring thread is still raised to user-interactive quality of service on macOS.
    ///     Set via <see cref="BenchmarkSuite.WithHardwareAffinity" /> /
    ///     <see cref="BenchmarkHarness.WithHardwareAffinity" />, the <c>--cpu-affinity</c> /
    ///     <c>--priority</c> / <c>--dedicated-host-guidance</c> CLI flags, or directly on
    ///     the options record.
    /// </summary>
    public EnvironmentOptions? Environment { get; init; }

    /// <summary>
    ///     The runtime-startup configuration to measure under - JIT tiering, dynamic PGO,
    ///     ReadyToRun and GC flavour. Defaults to <see cref="RuntimeProfile.SteadyState" />,
    ///     which is the only configuration measured to be both precise and accurate.
    ///     <para>
    ///         This can only be honoured for benchmarks that run in a child process, because the
    ///         runtime reads these settings once at startup. An in-process run reports
    ///         <see cref="RuntimeProfile.Host" /> on its results and carries a warning, rather
    ///         than claiming a fidelity it does not have. Set
    ///         <see cref="RuntimeProfile.Host" /> to opt out and inherit the host's configuration
    ///         everywhere.
    ///     </para>
    /// </summary>
    public RuntimeProfile RuntimeProfile { get; init; } = RuntimeProfile.SteadyState;

    /// <summary>
    ///     Suppresses the once-per-process guidance that fires when
    ///     <see cref="RuntimeProfile" /> was requested but could not be applied because the
    ///     measurement is running in the host process. Set this when in-process measurement is a
    ///     deliberate choice. The result's <see cref="BenchmarkResult.RuntimeProfileName" /> stamp
    ///     is unaffected - suppressing the message never suppresses the provenance.
    /// </summary>
    public bool SuppressRuntimeProfileWarning { get; init; }

    /// <summary>
    ///     Turns an isolation <b>refusal</b> into a thrown exception rather than a labelled fallback.
    ///     On by default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The default is <c>true</c> because the in-process fallback should be something a user asks
    ///         for, never something that happens to them. It was off while there was a great deal left to
    ///         refuse - a captured local, a prepared value, a scoped container and a parameter sweep over
    ///         a non-scalar each cost a run its isolation - and in that world a hard error would have been
    ///         a wall rather than a signal. Those shapes now cross, so what remains under this gate is a
    ///         genuinely small set, and every member of it has a remedy that fits on one line.
    ///     </para>
    ///     <para>
    ///         A refusal throws, carrying the refusal text, at the point of refusal - and in Harness mode
    ///         before <i>anything</i> has been measured, because the isolatability of every discovered
    ///         class is decided in one pass up front. That is the earliest a caller can act on it and the
    ///         cheapest place to fail.
    ///     </para>
    ///     <para>
    ///         It gates the four refusal statuses only, never <c>!IsIsolated()</c>: <c>--dry-run</c>,
    ///         <c>--in-process</c>, <c>[InProcess]</c>, <c>Benchmark.RunInProcess</c>,
    ///         <c>WithIsolation(false)</c> and <c>BenchmarkSuite.AddInProcess</c> all remain legal and
    ///         produce <see cref="IsolationStatus.InProcessRequested" />. Set this to <c>false</c> to go
    ///         back to a labelled fallback everywhere - which is still the right setting for the
    ///         scratchpad use Simple mode exists for, where a number measured in this process and clearly
    ///         stamped beats no number at all.
    ///     </para>
    ///     <para>
    ///         Excluded from serialization: this is a decision the coordinator makes before a worker
    ///         exists, and a worker that received it could do nothing with it. A coordinator-only field
    ///         travelling to a process that ignores it is how a setting comes to look effective while
    ///         being inert.
    ///     </para>
    /// </remarks>
    [JsonIgnore]
    public bool RequireIsolation { get; init; } = true;

    /// <summary>Creates options for the specified <paramref name="profile" />.</summary>
    public static MeasurementOptions For(MeasurementProfile profile) => new() { Profile = profile };

    /// <summary>
    ///     Resolves the effective outlier detector: the custom
    ///     <see cref="OutlierDetector" /> when supplied, otherwise the built-in detector for
    ///     the configured <see cref="OutlierMode" />.
    /// </summary>
    public IOutlierDetector ResolveOutlierDetector() =>
        OutlierDetector ?? OutlierDetectors.ForMode(OutlierMode);

    /// <summary>
    ///     Resolves the effective significance test: the custom
    ///     <see cref="SignificanceTest" /> when supplied, otherwise
    ///     <see cref="DefaultSignificanceTest" />.
    /// </summary>
    public ISignificanceTest ResolveSignificanceTest() =>
        SignificanceTest ?? DefaultSignificanceTest.Instance;

    internal static IReadOnlyList<double> NormalizePercentiles(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
            return Array.Empty<double>();

        var normalized = new List<double>(values.Count);

        foreach (var percentile in values)
        {
            if (!double.IsFinite(percentile) || percentile is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(values), values,
                    "ReportedPercentiles must contain only finite values between 0 and 1 inclusive.");
            }

            normalized.Add(percentile);
        }

        normalized.Sort();

        var deduped = new List<double>(normalized.Count);

        foreach (var percentile in normalized)
        {
            if (deduped.Count == 0 || Math.Abs(percentile - deduped[^1]) > PercentileEqualityTolerance)
                deduped.Add(percentile);
        }

        return Array.AsReadOnly(deduped.ToArray());
    }
}
