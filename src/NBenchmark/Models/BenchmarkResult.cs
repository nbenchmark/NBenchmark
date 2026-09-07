using NBenchmark.Stats;

namespace NBenchmark;

/// <summary>
///     The full outcome of measuring one benchmark: descriptive statistics, distribution shape,
///     allocation and diagnostics data, significance versus a baseline, and the provenance of how
///     and where it was measured.
/// </summary>
public record BenchmarkResult
{
    /// <summary>The benchmark's name, including any parameter suffix (e.g. <c>Sort(size=10)</c>).</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     The class that declared this benchmark. Empty when the benchmark was not
    ///     discovered from a class (for example, suite-mode entries added directly).
    /// </summary>
    public string ClassName { get; init; } = "";

    /// <summary>Optional free-text description of the benchmark, if one was supplied.</summary>
    public string? Description { get; init; }

    /// <summary>The mean per-op time, in nanoseconds, over the trimmed samples.</summary>
    public required double MeanNs { get; init; }

    /// <summary>The median per-op time, in nanoseconds, over the trimmed samples.</summary>
    public required double MedianNs { get; init; }

    /// <summary>
    ///     The minimum per-op time, in nanoseconds. Follows <see cref="TailMetricsBasis" />.
    /// </summary>
    public required double MinNs { get; init; }

    /// <summary>
    ///     The maximum per-op time, in nanoseconds. Follows <see cref="TailMetricsBasis" />.
    /// </summary>
    public required double MaxNs { get; init; }

    /// <summary>
    ///     Configurable percentile values computed from the trimmed samples.
    ///     Default set: P50 (0.50), P95 (0.95), P99 (0.99), P99.9 (0.999), max (1.0).
    ///     Controlled by <see cref="MeasurementOptions.ReportedPercentiles" />.
    ///     Sorted by percentile value ascending.
    /// </summary>
    public IReadOnlyList<PercentileEntry> Percentiles { get; init; } = [];

    /// <summary>
    ///     Latency histogram with bucket boundaries and sample counts.
    ///     <c>null</c> when <see cref="MeasurementOptions.EnableHistogram" /> is
    ///     <c>false</c> or when there are fewer than 2 samples.
    /// </summary>
    public LatencyHistogram? Histogram { get; init; }

    /// <summary>The standard deviation of the per-op time, in nanoseconds, over the trimmed samples.</summary>
    public required double StandardDeviationNs { get; init; }

    /// <summary>The standard error of the mean, in nanoseconds (<see cref="StandardDeviationNs" /> / √n).</summary>
    public double StandardErrorNs { get; init; }

    /// <summary>The half-width of the confidence interval on the mean, in nanoseconds, at <see cref="ConfidenceLevel" />.</summary>
    public double MarginOfErrorNs { get; init; }

    /// <summary>The confidence level (e.g. 0.95 for 95%) the mean's interval was computed at.</summary>
    public double ConfidenceLevel { get; init; } = 0.95;

    /// <summary>The coefficient of variation (<see cref="StandardDeviationNs" /> / <see cref="MeanNs" />) over the trimmed samples.</summary>
    public double CoefficientOfVariation { get; init; }

    /// <summary>The first quartile (25th percentile) of the per-op time, in nanoseconds.</summary>
    public required double Q1Ns { get; init; }

    /// <summary>The third quartile (75th percentile) of the per-op time, in nanoseconds.</summary>
    public required double Q3Ns { get; init; }

    /// <summary>The interquartile range (<see cref="Q3Ns" /> - <see cref="Q1Ns" />), in nanoseconds.</summary>
    public required double InterquartileRangeNs { get; init; }

    /// <summary>The lower outlier fence, in nanoseconds, used by the fence-based outlier detectors. <c>null</c> when not applicable to the active detector.</summary>
    public double? LowerFenceNs { get; init; }

    /// <summary>The upper outlier fence, in nanoseconds, used by the fence-based outlier detectors. <c>null</c> when not applicable to the active detector.</summary>
    public double? UpperFenceNs { get; init; }

    /// <summary>The number of raw samples the outlier detector discarded before computing statistics.</summary>
    public required int OutliersRemoved { get; init; }

    /// <summary>
    ///     The number of measured samples the statistics were computed from, after outlier
    ///     trimming. Add <see cref="OutliersRemoved" /> for the pre-trim count.
    /// </summary>
    public required int SampleCount { get; init; }

    /// <summary>
    ///     Ordinals (zero-based positions in the original raw-sample stream) of every sample
    ///     that the outlier detector discarded, sorted ascending by value (matching the order
    ///     of the discarded values themselves). Empty when no samples were trimmed or when
    ///     the result was not produced by the stats pipeline (dry-run, errored, or built
    ///     from a calibration factory).
    ///     <para>
    ///         Use this to flag individual raw samples as trimmed without re-running the
    ///         outlier detector. The ordinals refer to positions in
    ///         <see cref="RawSamples" /> when the result came from a measured run.
    ///     </para>
    /// </summary>
    public IReadOnlyList<int> TrimmedOrdinals { get; init; } = [];

    /// <summary>
    ///     The raw per-op nanoseconds of every measured sample, in sample order, before
    ///     outlier trimming. Empty for dry-run, errored, or calibration-derived results.
    ///     <see cref="TrimmedOrdinals" /> indexes into this collection.
    /// </summary>
    public IReadOnlyList<double> RawSamples { get; init; } = [];

    /// <summary>The skewness of the trimmed sample distribution. Not meaningful below 3 samples.</summary>
    public required double Skewness { get; init; }

    /// <summary>The excess kurtosis of the trimmed sample distribution. Not meaningful below 4 samples.</summary>
    public required double Kurtosis { get; init; }

    /// <summary>The median absolute deviation of the trimmed samples, in nanoseconds.</summary>
    public required double MedianAbsoluteDeviationNs { get; init; }

    /// <summary>
    ///     Lower bound of the distribution-free confidence interval on the median (order-statistic
    ///     interval at <see cref="ConfidenceLevel" />). <c>null</c> for dry-run, errored, or
    ///     calibration-derived results, or when there are fewer than two samples. Assumption-free,
    ///     unlike the t-interval on the mean - the median is the headline comparison metric.
    /// </summary>
    public double? MedianConfidenceIntervalLowerNs { get; init; }

    /// <summary>Upper bound of the median confidence interval. See <see cref="MedianConfidenceIntervalLowerNs" />.</summary>
    public double? MedianConfidenceIntervalUpperNs { get; init; }

    /// <summary>
    ///     The Hodges-Lehmann shift versus the baseline (median of pairwise candidate − baseline
    ///     differences) with a rank-based confidence interval, in nanoseconds per op. Positive means
    ///     the candidate is slower. Populated during significance testing for non-baseline results;
    ///     <c>null</c> for the baseline, single-benchmark runs, or when significance did not run.
    /// </summary>
    public ShiftEstimate? MedianShift { get; init; }

    /// <summary>The median per-op allocation, in bytes. <c>null</c> when allocation tracking was off or unavailable.</summary>
    public required long? AllocatedBytesMedian { get; init; }

    /// <summary>The P95 per-op allocation, in bytes. <c>null</c> when allocation tracking was off or unavailable.</summary>
    public required long? AllocatedBytesP95 { get; init; }

    /// <summary>The maximum per-op allocation, in bytes. <c>null</c> when allocation tracking was off or unavailable.</summary>
    public required long? AllocatedBytesMax { get; init; }

    /// <summary>The mean per-op allocation, in bytes. <c>null</c> when allocation tracking was off or unavailable.</summary>
    public long? AllocatedBytesMean { get; init; }

    /// <summary>
    ///     The mean number of operations per second, computed as 1e9 / MeanNs, where the mean is
    ///     measured in nanoseconds per operation. NaN for errored or dry-run results.
    /// </summary>
    public double OperationsPerSecond { get; init; }

    /// <summary>
    ///     The median number of operations per second, computed as 1e9 / MedianNs. NaN for errored
    ///     or dry-run results.
    /// </summary>
    public double MedianOperationsPerSecond { get; init; }

    /// <summary>
    ///     Total body invocations executed across warmup and measurement. When auto-tuning is
    ///     active this mirrors <see cref="AutoTuneDiagnostic.TotalBodyInvocations" />; otherwise
    ///     it is the sum of measured and warmup samples.
    /// </summary>
    public long TotalOperations { get; init; }

    /// <summary>The p-value from the pairwise significance test versus the baseline. <c>null</c> when not tested.</summary>
    public double? PValue { get; init; }

    /// <summary>The pooled significance verdict versus the baseline. <see cref="NBenchmark.SignificanceVerdict.NotTested" /> when significance testing did not run.</summary>
    public SignificanceVerdict SignificanceVerdict { get; init; }

    /// <summary>
    ///     The verdict of the <b>launch-blocked</b> paired test on the per-launch medians, reported
    ///     alongside the pooled <see cref="SignificanceVerdict" />.
    ///     <para>
    ///         The pooled verdict runs on every raw sample concatenated across launches, so it
    ///         inherits the power of the pooled count and flags a between-launch location offset at
    ///         full n regardless of whether the code differs. The launch-blocked verdict reuses
    ///         <see cref="LogRatio.Estimate(NBenchmark.BenchmarkResult,NBenchmark.BenchmarkResult)" />
    ///         - a one-sample Student-t on the per-launch log-ratios over the k paired launches - so
    ///         it answers the question a reader is actually asking: does the difference reproduce
    ///         across launches, or is it a single process draw read as a code change?
    ///         <c>NotTested</c> when fewer than two launches can be paired (single-launch runs).
    ///     </para>
    /// </summary>
    public SignificanceVerdict LaunchBlockedVerdict { get; init; }

    /// <summary>
    ///     Optional effect-size payload produced by the active significance strategy.
    ///     Built-in Mann-Whitney strategies populate this with Cliff's delta and a
    ///     Romano magnitude label.
    /// </summary>
    public EffectSize? Effect { get; init; }

    /// <summary>
    ///     The omnibus significance verdict (e.g. Kruskal-Wallis) shared across all
    ///     benchmarks in the comparison, when an omnibus test was run (three or more groups).
    ///     <c>null</c> for pairwise comparisons.
    /// </summary>
    public OmnibusComparison? Omnibus { get; init; }

    /// <summary>
    ///     The display name of the significance strategy used (e.g. <c>"Mann-Whitney U"</c>).
    ///     Reflects a custom <see cref="MeasurementOptions.SignificanceTest" /> when one is
    ///     configured.
    /// </summary>
    public string SignificanceTestName { get; init; } = DefaultSignificanceTest.Instance.Name;

    /// <summary>The significance level (alpha) this result was tested against. Default 0.05.</summary>
    public double SignificanceLevel { get; init; } = 0.05;

    /// <summary>Whether the benchmark threw during measurement. When <c>true</c>, statistics fields are zeroed and <see cref="ErrorMessage" /> carries the reason.</summary>
    public bool Errored { get; init; }

    /// <summary>The exception message, when <see cref="Errored" /> is <c>true</c>; otherwise <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The number of warmup samples discarded before measurement began.</summary>
    public int WarmupSamples { get; init; }

    /// <summary>When this benchmark's measurement started.</summary>
    public DateTimeOffset RunAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The total wall-clock time spent on this benchmark, including warmup and calibration.</summary>
    public TimeSpan TotalDuration { get; init; } = TimeSpan.Zero;

    /// <summary>The wall-clock time spent in the measurement phase only, excluding warmup and calibration.</summary>
    public TimeSpan MeasuredDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Whether this result is the baseline of its comparison group.</summary>
    public bool IsBaseline { get; init; }

    /// <summary>
    ///     Categories assigned to this benchmark through class-level and method-level
    ///     <see cref="NBenchmark.BenchmarkCategoryAttribute" />. Empty when no
    ///     categories were declared.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    ///     The target framework moniker (e.g. "net8.0", "net9.0") under which this
    ///     benchmark was executed. Empty when the runtime is not explicitly specified
    ///     (single-runtime runs).
    /// </summary>
    public string TargetFramework { get; init; } = "";

    /// <summary>The built-in outlier-trimming strategy used, when no custom detector was configured.</summary>
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>
    ///     The display name of the outlier detector that produced this result (e.g.
    ///     <c>"IQR fence (1.5×)"</c>). Reflects a custom
    ///     <see cref="MeasurementOptions.OutlierDetector" /> when one is configured.
    /// </summary>
    public string OutlierDetectorName { get; init; } = OutlierDetectors.IqrFence.Name;

    /// <summary>
    ///     Which sample set the order statistics on this result were computed from -
    ///     <see cref="NBenchmark.TailMetricsBasis.Raw" /> (the full pre-trim distribution, the default) or
    ///     <see cref="NBenchmark.TailMetricsBasis.Trimmed" />.
    ///     <para>
    ///         This matters for anything that displays these numbers. <see cref="MinNs" />,
    ///         <see cref="MaxNs" />, <see cref="Percentiles" /> and <see cref="Histogram" /> follow this
    ///         basis, while <see cref="MeanNs" />, <see cref="MedianNs" />,
    ///         <see cref="StandardDeviationNs" />, the confidence intervals and <see cref="SampleCount" /> are
    ///         always computed on the trimmed set. Under the default basis the two describe different
    ///         populations and are not comparable - a consumer that shows both must say which is which.
    ///     </para>
    /// </summary>
    public TailMetricsBasis TailMetricsBasis { get; init; } = TailMetricsBasis.Raw;

    /// <summary>The measurement profile under which this result was produced.</summary>
    public GcBehavior GcBehavior { get; init; } = GcBehavior.Natural;

    /// <summary>
    ///     The name of the runtime-startup configuration this result was <b>actually</b> measured
    ///     under - not the one that was requested. <c>"host"</c> means the measurement ran in a
    ///     process NBenchmark did not launch, so it inherited whatever runtime configuration that
    ///     process was started with; every in-process result reports this.
    ///     <para>
    ///         Two results measured under different runtime profiles are not comparable, so the
    ///         significance engine never places them in the same comparison group.
    ///     </para>
    /// </summary>
    public string RuntimeProfileName { get; init; } = RuntimeProfile.Host.Name;

    /// <summary>
    ///     The runtime-startup knobs in effect for this measurement, e.g.
    ///     <c>"tiered=off pgo=off r2r=off"</c>. Read from the measuring process's own environment
    ///     rather than derived from the requested profile, so a knob the user set by hand is
    ///     reported as faithfully as one NBenchmark applied. Empty when none are set.
    /// </summary>
    public string RuntimeKnobs { get; init; } = "";

    /// <summary>
    ///     Whether thread-level environment control was enabled for this measurement:
    ///     thread affinity, thread priority, and (on macOS) the
    ///     <c>QOS_CLASS_USER_INTERACTIVE</c> elevation that keeps the measuring thread on
    ///     an Apple Silicon performance core. On by default; set to <c>false</c> via
    ///     <c>--no-thread-control</c> / <c>.WithThreadControl(false)</c>. Read from the
    ///     <see cref="MeasurementOptions.Environment" /> that produced this result, so an
    ///     in-process run reports the setting the host used. This records the configured
    ///     setting, not the application outcome: on macOS the elevation is refused on
    ///     thread-pool threads (the default worker path), and that refusal is reported via
    ///     the console warning rather than this field. Errored and dry-run rows report the
    ///     options they were built with; only rows built without options (e.g. calibration
    ///     rows) inherit the <see cref="MeasurementOptions.Default" /> value.
    /// </summary>
    public bool ThreadControlEnabled { get; init; } = true;

    /// <summary>
    ///     Whether the evidence-based interference rejection filter was enabled for this
    ///     measurement - the pre-stage that discards samples the OS is known to have
    ///     preempted (per-sample CPU-occupancy ratio materially below this benchmark's own
    ///     median) before <see cref="OutlierMode" /> trimming runs. On by default; set to
    ///     <c>false</c> via <c>--no-interference-filter</c> /
    ///     <c>.WithInterferenceFilter(false)</c>. Distinct from
    ///     <see cref="AutoTuneDiagnostic.InterferenceDisabledReason" />, which records
    ///     when the filter was <i>involuntarily</i> disabled by the engine (unsupported
    ///     platform, probe too costly, too few known-occupancy samples); this field
    ///     records the user's configured setting. Errored and dry-run rows report the
    ///     options they were built with; only rows built without options (e.g. calibration
    ///     rows) inherit the <see cref="MeasurementOptions.Default" /> value.
    /// </summary>
    public bool InterferenceFilterEnabled { get; init; } = true;

    /// <summary>
    ///     Where this measurement ran, and - when it did not run in a worker - why not.
    ///     <para>
    ///         The default is <see cref="IsolationStatus.InProcessRequested" /> rather than
    ///         <see cref="IsolationStatus.Isolated" /> on purpose: a result that nobody explicitly
    ///         marked as isolated did not come from a worker, and defaulting the other way would let
    ///         any code path that forgot to set it claim a fidelity it never had.
    ///     </para>
    ///     <para>
    ///         Note that this initializer is the <i>whole</i> of that guarantee.
    ///         <see cref="IsolationStatus.Isolated" /> is <c>0</c>, so <c>default(IsolationStatus)</c>
    ///         is the permissive value - the enum cannot be renumbered to fix that, because its values
    ///         travel on the wire inside this record. Every measurement therefore starts here as
    ///         host-measured and is re-stamped by the layer that knows better, at
    ///         <c>WorkerGroupRunner</c> for the streaming path and via <c>with</c> expressions
    ///         elsewhere. Removing the initializer would silently promote every un-stamped result.
    ///         Pinned by <c>BenchmarkResultTests.IsolationStatus_DefaultsToHostMeasured</c>.
    ///     </para>
    /// </summary>
    public IsolationStatus IsolationStatus { get; init; } = IsolationStatus.InProcessRequested;

    /// <summary>Setup or measurement warnings surfaced for this benchmark. Empty when there are none.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    ///     The parameter values for this benchmark run, if part of a parameterized suite.
    ///     Empty when no parameters were defined.
    /// </summary>
    public IReadOnlyList<BenchmarkParameter> ParameterSet { get; init; } = [];

    /// <summary>
    ///     Diagnostics from the adaptive measurement loop: the resolved warmup and sample counts,
    ///     the calibrated ops-per-sample, why each phase stopped, and the achieved CI width.
    ///     <c>null</c> for dry-run and errored results.
    /// </summary>
    public AutoTuneDiagnostic? AutoTune { get; init; }

    /// <summary>
    ///     Cross-launch summary statistics, populated when the launch count is above one.
    ///     <c>null</c> when the benchmark ran a single launch.
    /// </summary>
    public LaunchStatistics? LaunchStatistics { get; init; }

    /// <summary>
    ///     Runtime diagnostics collected during measurement: GC collection counts, heap info,
    ///     exception rates, and CPU time. <c>null</c> when no diagnostics were collected or
    ///     the run errored.
    /// </summary>
    public DiagnosticsResult? Diagnostics { get; init; }

    /// <summary>
    ///     Where in the run this benchmark was measured and how fast the host was at that point,
    ///     from the drift canary's bracketing readings. <c>null</c> when the canary was off
    ///     (<c>--no-drift-canary</c>), for dry-run and errored results, and whenever a bracketing
    ///     reading was unusable.
    /// </summary>
    public HostTimeline? HostTimeline { get; init; }

    /// <summary>Lower bound of the confidence interval on the mean, in nanoseconds.</summary>
    public double ConfidenceIntervalLowerNs => MeanNs - MarginOfErrorNs;

    /// <summary>Upper bound of the confidence interval on the mean, in nanoseconds.</summary>
    public double ConfidenceIntervalUpperNs => MeanNs + MarginOfErrorNs;

    /// <summary>The spread between <see cref="MaxNs" /> and <see cref="MinNs" />, in nanoseconds.</summary>
    public double RangeNs => MaxNs - MinNs;

    /// <summary><see cref="StandardErrorNs" /> as a percentage of <see cref="MeanNs" />; 0 when the mean is not positive.</summary>
    public double StandardErrorPercent => MeanNs > 0 ? StandardErrorNs / MeanNs * 100 : 0;

    /// <summary><see cref="MarginOfErrorNs" /> as a percentage of <see cref="MeanNs" />; 0 when the mean is not positive.</summary>
    public double MarginOfErrorPercent => MeanNs > 0 ? MarginOfErrorNs / MeanNs * 100 : 0;

    /// <summary><see cref="CoefficientOfVariation" /> expressed as a percentage.</summary>
    public double CoefficientOfVariationPercent => CoefficientOfVariation * 100;

    /// <summary>
    ///     Convenience accessor for a specific percentile value.
    ///     Returns the value for the first entry whose percentile matches
    ///     <paramref name="p" /> within a 1e-9 tolerance, or <c>null</c> if
    ///     the requested percentile was not computed.
    /// </summary>
    public double? GetPercentile(double p)
    {
        foreach (var e in Percentiles)
        {
            if (Math.Abs(e.Percentile - p) < 1e-9)
                return e.Value;
        }

        return null;
    }

    /// <summary>
    ///     Factory that produces a <see cref="BenchmarkResult" /> from a calibration
    ///     benchmark's raw timings. Used by the test-integration comparison path.
    ///     <para>
    ///         The provided <paramref name="mean" /> and <paramref name="median" /> are kept
    ///         as-supplied (the test-integration caller computes them independently of
    ///         <see cref="StatsSummary" />). The remaining descriptive statistics - standard
    ///         deviation, standard error, margin of error, confidence level, min, max, skewness,
    ///         kurtosis, MAD, and the quartiles - are computed from <paramref name="samples" />
    ///         rather than reported as zeros, so the result mirrors a real benchmark's shape and
    ///         feeds the test-integration comparison path with honest numbers.
    ///     </para>
    /// </summary>
    public static BenchmarkResult FromCalibration(
        string name, double mean, double median, IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return new BenchmarkResult
            {
                Name = name,
                MeanNs = mean,
                MedianNs = median,
                MinNs = 0,
                MaxNs = 0,
                StandardDeviationNs = 0,
                Q1Ns = 0,
                Q3Ns = 0,
                InterquartileRangeNs = 0,
                OutliersRemoved = 0,
                SampleCount = 0,
                Skewness = 0,
                Kurtosis = 0,
                MedianAbsoluteDeviationNs = 0,
                AllocatedBytesMedian = null,
                AllocatedBytesP95 = null,
                AllocatedBytesMax = null,
            };
        }

        // Compute the full descriptive-statistics summary from the samples. Disable the
        // histogram and reported percentiles - FromCalibration is not a measured run, so the
        // result carries no histogram and the default percentile set is left empty to match the
        // previous behaviour and avoid surprising the test-integration comparison path.
        // A copy the caller cannot reach, so nothing downstream depends on a list the caller may
        // still be mutating - and the sort below has an array to work on either way.
        var values = samples.ToArray();
        var stats = StatsSummary.Compute(values, enableHistogram: false, reportedPercentiles: []);

        // Quartiles use the same nearest-rank convention the stats pipeline uses for the raw
        // sample set (OutlierTrim computes Q1Ns/Q3Ns on the raw, pre-trim array). StatsSummary
        // does not surface Q1Ns/Q3Ns, so compute them on the sorted samples StatsSummary already
        // normalised internally. Build a sorted copy so the public FromCalibration contract
        // (the caller's samples are never reordered) holds.
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var q1 = Percentile.Compute(sorted, 0.25);
        var q3 = Percentile.Compute(sorted, 0.75);

        return new BenchmarkResult
        {
            Name = name,
            MeanNs = mean,
            MedianNs = median,
            MinNs = stats.MinNs,
            MaxNs = stats.MaxNs,
            StandardDeviationNs = stats.StandardDeviationNs,
            StandardErrorNs = stats.StandardErrorNs,
            MarginOfErrorNs = stats.MarginOfErrorNs,
            ConfidenceLevel = stats.ConfidenceLevel,
            Q1Ns = q1,
            Q3Ns = q3,
            InterquartileRangeNs = q3 - q1,
            OutliersRemoved = 0,
            SampleCount = values.Length,
            Skewness = stats.Skewness,
            Kurtosis = stats.Kurtosis,
            MedianAbsoluteDeviationNs = stats.MedianAbsoluteDeviationNs,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };
    }
}
