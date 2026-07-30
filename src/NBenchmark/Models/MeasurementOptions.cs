using System.Text.Json.Serialization;
using NBenchmark.Stats;

namespace NBenchmark;

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
    public const int MaxLaunchCount = 100;
    public const int MinHistogramBucketCount = 5;
    public const int MaxHistogramBucketCount = 100;

    /// <summary>
    ///     Default ceiling on how many raw samples an isolated worker sends back per benchmark.
    ///     See <see cref="MaxRawSamples" />.
    /// </summary>
    public const int DefaultMaxRawSamples = 4096;

    /// <summary><see cref="MaxRawSamples" /> value meaning "return every sample".</summary>
    public const int UnboundedRawSamples = 0;

    internal static readonly IReadOnlyList<double> DefaultReportedPercentiles =
        Array.AsReadOnly(new[] { 0.50, 0.95, 0.99, 0.999, 1.0 });

    public static readonly MeasurementOptions Default = new();
    private readonly double _confidenceLevel = 0.95;
    private readonly int _histogramBucketCount = 20;
    private readonly int _maxRawSamples = DefaultMaxRawSamples;
    private readonly int? _iterations;
    private readonly int _launchCount = 1;
    private readonly double? _minimumPracticalEffect = DefaultMinimumPracticalEffect;

    /// <summary>
    ///     The default <see cref="MinimumPracticalEffect" />: 0.147, the Romano boundary between a
    ///     negligible and a small effect (the same threshold the Magnitude column uses). A ✓ verdict
    ///     therefore means "statistically real <em>and</em> at least a small effect". Set the option
    ///     to <c>0</c> to restore p-value-only verdicts.
    /// </summary>
    public const double DefaultMinimumPracticalEffect = 0.147;
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
    ///     Number of times to repeat the benchmark as separate launches.
    ///     1 (default) runs the benchmark once. Higher values trigger per-launch
    ///     aggregation and populate <see cref="BenchmarkResult.LaunchStatistics" />.
    ///     Must be between 1 and <see cref="MaxLaunchCount" />.
    /// </summary>
    public int LaunchCount
    {
        get => _launchCount;
        init
        {
            if (value is < 1 or > MaxLaunchCount)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"LaunchCount must be between 1 and {MaxLaunchCount}.");
            }

            _launchCount = value;
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
    ///     Opt-in hardware/OS controls applied for the duration of a run: CPU affinity,
    ///     process priority, and dedicated-host guidance. <c>null</c> (the default) does
    ///     nothing - the benchmark runs with whatever affinity and priority the host
    ///     started it with. Set via <see cref="BenchmarkSuite.WithHardwareAffinity" /> /
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
