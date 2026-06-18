using NBenchmark.Stats;

namespace NBenchmark;

public record BenchmarkResult
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double P95 { get; init; }
    public required double P99 { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
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

    public required double Skewness { get; init; }
    public required double Kurtosis { get; init; }
    public required double Mad { get; init; }

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

    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>
    ///     The display name of the outlier detector that produced this result (e.g.
    ///     <c>"IQR fence (1.5×)"</c>). Reflects a custom
    ///     <see cref="MeasurementOptions.OutlierDetector" /> when one is configured.
    /// </summary>
    public string OutlierDetector { get; init; } = OutlierDetectors.IqrFence.Name;

    /// <summary>The measurement profile under which this result was produced.</summary>
    public MeasurementProfile Profile { get; init; } = MeasurementProfile.Realistic;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    ///     Diagnostics from the adaptive measurement loop: the resolved warmup and sample counts,
    ///     the calibrated ops-per-sample, why each phase stopped, and the achieved CI width.
    ///     <c>null</c> for dry-run and errored results.
    /// </summary>
    public AutoTuneDiagnostic? AutoTune { get; init; }

    public double ConfidenceIntervalLower => Mean - MarginOfError;
    public double ConfidenceIntervalUpper => Mean + MarginOfError;
    public double Range => Max - Min;
    public double StandardErrorPercent => Mean > 0 ? StandardError / Mean * 100 : 0;
    public double MarginPercent => Mean > 0 ? MarginOfError / Mean * 100 : 0;
    public double CoefficientOfVariationPercent => CoefficientOfVariation * 100;
}
