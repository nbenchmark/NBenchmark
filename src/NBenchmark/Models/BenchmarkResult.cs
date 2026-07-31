using NBenchmark.Stats;

namespace NBenchmark;

public record BenchmarkResult
{
    public required string Name { get; init; }

    /// <summary>
    ///     The class that declared this benchmark. Empty when the benchmark was not
    ///     discovered from a class (for example, suite-mode entries added directly).
    /// </summary>
    public string ClassName { get; init; } = "";

    public string? Description { get; init; }

    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }

    /// <summary>
    ///     Configurable percentile values computed from the trimmed samples.
    ///     Default set: P50 (0.50), P95 (0.95), P99 (0.99), P99.9 (0.999), Max (1.0).
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

    public required double StandardDeviation { get; init; }

    public double StandardError { get; init; }

    public double MarginOfError { get; init; }

    public double ConfidenceLevel { get; init; } = 0.95;

    public double CoefficientOfVariation { get; init; }

    public required double Q1 { get; init; }
    public required double Q3 { get; init; }
    public required double InterquartileRange { get; init; }

    public double? LowerFence { get; init; }
    public double? UpperFence { get; init; }

    public required int OutliersRemoved { get; init; }
    public required int N { get; init; }

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
    ///     <see cref="TrimmedOrdinals" /> indexes into this collection. Shares storage with
    ///     <see cref="MeasurementOutcome.RawSamples" /> when the result came from
    ///     <c>Benchmark.RunRaw</c> / <c>RunRawAsync</c>; treat as read-only.
    /// </summary>
    public IReadOnlyList<double> RawSamples { get; init; } = [];

    public required double Skewness { get; init; }
    public required double Kurtosis { get; init; }
    public required double Mad { get; init; }

    /// <summary>
    ///     Lower bound of the distribution-free confidence interval on the median (order-statistic
    ///     interval at <see cref="ConfidenceLevel" />). <c>null</c> for dry-run, errored, or
    ///     calibration-derived results, or when there are fewer than two samples. Assumption-free,
    ///     unlike the t-interval on the mean - the median is the headline comparison metric.
    /// </summary>
    public double? MedianCiLower { get; init; }

    /// <summary>Upper bound of the median confidence interval. See <see cref="MedianCiLower" />.</summary>
    public double? MedianCiUpper { get; init; }

    /// <summary>
    ///     The Hodges-Lehmann shift versus the baseline (median of pairwise candidate − baseline
    ///     differences) with a rank-based confidence interval, in nanoseconds per op. Positive means
    ///     the candidate is slower. Populated during significance testing for non-baseline results;
    ///     <c>null</c> for the baseline, single-benchmark runs, or when significance did not run.
    /// </summary>
    public ShiftEstimate? MedianShift { get; init; }

    public required long? AllocMedian { get; init; }
    public required long? AllocP95 { get; init; }
    public required long? AllocMax { get; init; }

    public long? MeanAllocatedBytes { get; init; }

    /// <summary>
    ///     The mean number of operations per second, computed as 1e9 / Mean where the mean is
    ///     measured in nanoseconds per operation. NaN for errored or dry-run results.
    /// </summary>
    public double OperationsPerSecond { get; init; }

    /// <summary>
    ///     The median number of operations per second, computed as 1e9 / Median. NaN for errored
    ///     or dry-run results.
    /// </summary>
    public double MedianOperationsPerSecond { get; init; }

    /// <summary>
    ///     Convenience alias for Mean, expressed as nanoseconds per operation. Identical to
    ///     <see cref="Mean" />.
    /// </summary>
    public double NanosecondsPerOperation => Mean;

    /// <summary>
    ///     Total body invocations executed across warmup and measurement. When auto-tuning is
    ///     active this mirrors <see cref="AutoTuneDiagnostic.TotalBodyInvocations" />; otherwise
    ///     it is the sum of measured and warmup iterations.
    /// </summary>
    public long TotalOperations { get; init; }

    public double? PValue { get; init; }
    public SignificanceVerdict SignificanceVerdict { get; init; }

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

    public double SignificanceLevel { get; init; } = 0.05;

    public bool Errored { get; init; }
    public string? ErrorMessage { get; init; }

    public int MeasuredIterations { get; init; }
    public int WarmupIterations { get; init; }
    public DateTimeOffset RunAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public TimeSpan TotalDuration { get; init; } = TimeSpan.Zero;

    public TimeSpan MeasuredDuration { get; init; } = TimeSpan.Zero;

    public bool IsBaseline { get; init; }

    /// <summary>
    ///     Categories assigned to this benchmark through class-level and method-level
    ///     <see cref="NBenchmark.Attributes.BenchmarkCategoryAttribute" />. Empty when no
    ///     categories were declared.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    ///     The target framework moniker (e.g. "net8.0", "net9.0") under which this
    ///     benchmark was executed. Empty when the runtime is not explicitly specified
    ///     (single-runtime runs).
    /// </summary>
    public string RuntimeMoniker { get; init; } = "";

    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>
    ///     The display name of the outlier detector that produced this result (e.g.
    ///     <c>"IQR fence (1.5×)"</c>). Reflects a custom
    ///     <see cref="MeasurementOptions.OutlierDetector" /> when one is configured.
    /// </summary>
    public string OutlierDetector { get; init; } = OutlierDetectors.IqrFence.Name;

    /// <summary>
    ///     Which sample set the order statistics on this result were computed from -
    ///     <see cref="NBenchmark.TailMetricsBasis.Raw" /> (the full pre-trim distribution, the default) or
    ///     <see cref="NBenchmark.TailMetricsBasis.Trimmed" />.
    ///     <para>
    ///         This matters for anything that displays these numbers. <see cref="Min" />,
    ///         <see cref="Max" />, <see cref="Percentiles" /> and <see cref="Histogram" /> follow this
    ///         basis, while <see cref="Mean" />, <see cref="Median" />,
    ///         <see cref="StandardDeviation" />, the confidence intervals and <see cref="N" /> are
    ///         always computed on the trimmed set. Under the default basis the two describe different
    ///         populations and are not comparable - a consumer that shows both must say which is which.
    ///     </para>
    /// </summary>
    public TailMetricsBasis TailMetricsBasis { get; init; } = TailMetricsBasis.Raw;

    /// <summary>The measurement profile under which this result was produced.</summary>
    public MeasurementProfile Profile { get; init; } = MeasurementProfile.Realistic;

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

    public double ConfidenceIntervalLower => Mean - MarginOfError;
    public double ConfidenceIntervalUpper => Mean + MarginOfError;
    public double Range => Max - Min;
    public double StandardErrorPercent => Mean > 0 ? StandardError / Mean * 100 : 0;
    public double MarginPercent => Mean > 0 ? MarginOfError / Mean * 100 : 0;
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
    public static BenchmarkResult FromCalibration(string name, double mean, double median, double[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Length == 0)
        {
            return new BenchmarkResult
            {
                Name = name,
                Mean = mean,
                Median = median,
                Min = 0,
                Max = 0,
                StandardDeviation = 0,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                MeasuredIterations = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            };
        }

        // Compute the full descriptive-statistics summary from the samples. Disable the
        // histogram and reported percentiles - FromCalibration is not a measured run, so the
        // result carries no histogram and the default percentile set is left empty to match the
        // previous behaviour and avoid surprising the test-integration comparison path.
        var stats = StatsSummary.Compute(samples, enableHistogram: false, reportedPercentiles: []);

        // Quartiles use the same nearest-rank convention the stats pipeline uses for the raw
        // sample set (OutlierTrim computes Q1/Q3 on the raw, pre-trim array). StatsSummary
        // does not surface Q1/Q3, so compute them on the sorted samples StatsSummary already
        // normalised internally. Build a sorted copy so the public FromCalibration contract
        // (the input array is never mutated) holds.
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);

        var q1 = Percentile.Compute(sorted, 0.25);
        var q3 = Percentile.Compute(sorted, 0.75);

        return new BenchmarkResult
        {
            Name = name,
            Mean = mean,
            Median = median,
            Min = stats.Min,
            Max = stats.Max,
            StandardDeviation = stats.StandardDeviation,
            StandardError = stats.StandardError,
            MarginOfError = stats.MarginOfError,
            ConfidenceLevel = stats.ConfidenceLevel,
            Q1 = q1,
            Q3 = q3,
            InterquartileRange = q3 - q1,
            OutliersRemoved = 0,
            N = samples.Length,
            MeasuredIterations = samples.Length,
            Skewness = stats.Skewness,
            Kurtosis = stats.Kurtosis,
            Mad = stats.Mad,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };
    }
}
