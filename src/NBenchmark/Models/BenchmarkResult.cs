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

    /// <summary>Standard error of the mean: <c>StandardDeviation / sqrt(n)</c>.</summary>
    public double StandardError { get; init; }

    /// <summary>
    ///     Half-width of the confidence interval on the mean at <see cref="ConfidenceLevel" />.
    ///     The interval is <c>Mean ± MarginOfError</c>.
    /// </summary>
    public double MarginOfError { get; init; }

    /// <summary>The confidence level for <see cref="MarginOfError" /> (e.g. 0.95). Default 0.95.</summary>
    public double ConfidenceLevel { get; init; } = 0.95;

    /// <summary>Coefficient of variation: <c>StandardDeviation / Mean</c> (0 when mean is 0).</summary>
    public double CoefficientOfVariation { get; init; }

    /// <summary>Lower bound of the confidence interval on the mean.</summary>
    public double ConfidenceIntervalLower => Mean - MarginOfError;

    /// <summary>Upper bound of the confidence interval on the mean.</summary>
    public double ConfidenceIntervalUpper => Mean + MarginOfError;

    public long? MeanAllocatedBytes { get; init; }

    public double? PValue { get; init; }
    public bool? IsSignificant { get; init; }

    public bool Errored { get; init; }
    public string? ErrorMessage { get; init; }

    public int MeasuredIterations { get; init; }
    public int WarmupIterations { get; init; }
    public DateTimeOffset RunAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     End-to-end wall-clock cost of running this benchmark entry, including warmup,
    ///     pre-measure <see cref="GC.Collect" />, and the measured loop. Stopped at the
    ///     end of the measured loop, so it excludes outlier-trim and stats compute
    ///     (microseconds, not a useful run-budget signal). Populated uniformly across
    ///     success, dry-run, and errored paths; <see cref="TimeSpan.Zero" /> is reserved
    ///     for failure sites that did not start a per-benchmark timer (e.g. suite setup).
    /// </summary>
    public TimeSpan TotalDuration { get; init; } = TimeSpan.Zero;

    /// <summary>
    ///     Wall-clock cost of the measured loop only, including per-iteration
    ///     <c>IterationSetup</c> / <c>IterationTeardown</c>, per-iteration
    ///     <see cref="GC.Collect" />, and allocation-tracking work. Excludes warmup
    ///     and the pre-measure <see cref="GC.Collect" />. Always
    ///     <see cref="TimeSpan.Zero" /> when no measured loop ran (dry-run, errored
    ///     before measurement).
    /// </summary>
    public TimeSpan MeasuredDuration { get; init; } = TimeSpan.Zero;

    public bool IsBaseline { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;
}