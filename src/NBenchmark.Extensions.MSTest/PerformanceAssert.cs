using Microsoft.VisualStudio.TestTools.UnitTesting;
using NBenchmark.Extensions.Abstractions;

namespace NBenchmark.Extensions.MSTest;

public static class PerformanceAssert
{
    public static BenchmarkResult Run(
        Action action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var result = Benchmark.Run(action, BuildMeasurementOptions(resolvedOptions), name, cancellationToken);
        Validate(result, resolvedOptions);
        return result;
    }

    public static BenchmarkResult Run<T>(
        Func<T> action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var result = Benchmark.Run(action, BuildMeasurementOptions(resolvedOptions), name, cancellationToken);
        Validate(result, resolvedOptions);
        return result;
    }

    public static async Task<BenchmarkResult> RunAsync(
        Func<Task> action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var result = await Benchmark.RunAsync(action, BuildMeasurementOptions(resolvedOptions), name, cancellationToken)
            .ConfigureAwait(false);
        Validate(result, resolvedOptions);
        return result;
    }

    public static async Task<BenchmarkResult> RunAsync<T>(
        Func<Task<T>> action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var result = await Benchmark.RunAsync(action, BuildMeasurementOptions(resolvedOptions), name, cancellationToken)
            .ConfigureAwait(false);
        Validate(result, resolvedOptions);
        return result;
    }

    public static void Validate(BenchmarkResult result, PerformanceAssertionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var violations = CollectViolations(result, resolvedOptions);

        if (violations.Count == 0)
            return;

        Assert.Fail(BuildFailureMessage(result, violations));
    }

    private static MeasurementOptions BuildMeasurementOptions(PerformanceAssertionOptions options)
    {
        var measurementOptions = MeasurementOptions.Default;

        if (options.Iterations > 0)
            measurementOptions = measurementOptions with { Iterations = options.Iterations };

        if (options.WarmupIterations > 0)
            measurementOptions = measurementOptions with { WarmupIterations = options.WarmupIterations };

        if (options.MeasureAllocations || options.MaxAllocatedBytes >= 0)
            measurementOptions = measurementOptions with { MeasureAllocations = true };

        measurementOptions = measurementOptions with
        {
            OutlierMode = NormalizeOutlierMode(options.OutlierMode),
            ConfidenceLevel = options.ConfidenceLevel is > 0 and <= 1 ? options.ConfidenceLevel : 0.95,
        };

        return measurementOptions;
    }

    private static List<string> CollectViolations(BenchmarkResult result, PerformanceAssertionOptions options)
    {
        var violations = new List<string>();

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = options.MaxMeanNs >= 0 ? options.MaxMeanNs : null,
            MaxP95Ns = options.MaxP95Ns >= 0 ? options.MaxP95Ns : null,
            MaxAllocatedBytes = options.MaxAllocatedBytes >= 0 ? options.MaxAllocatedBytes : null,
            BaselinePath = options.BaselinePath,
            MaxSlowdownRatio = options.MaxSlowdownRatio > 0 ? options.MaxSlowdownRatio : 1.2,
            Iterations = options.Iterations,
            WarmupIterations = options.WarmupIterations,
        };

        violations.AddRange(BenchmarkAssert.Validate(result, thresholds));

        if (!string.IsNullOrWhiteSpace(options.BaselinePath))
            violations.AddRange(RegressionBaseline.Check(result, options.BaselinePath!, thresholds.MaxSlowdownRatio));

        return violations;
    }

    private static string BuildFailureMessage(BenchmarkResult result, IReadOnlyList<string> violations)
    {
        return $"Performance assertions failed for '{result.Name}'.{Environment.NewLine}" +
               $"{string.Join(Environment.NewLine, violations)}{Environment.NewLine}{Environment.NewLine}" +
               FormatMetrics(result);
    }

    private static string FormatMetrics(BenchmarkResult result)
    {
        var allocations = result.MeanAllocatedBytes.HasValue
            ? $"{result.MeanAllocatedBytes.Value} B"
            : "n/a";

        return
            $"NBenchmark metrics{Environment.NewLine}" +
            $"Mean: {result.Mean:F2} ns{Environment.NewLine}" +
            $"P95: {result.P95:F2} ns{Environment.NewLine}" +
            $"Allocations: {allocations}{Environment.NewLine}" +
            $"Iterations: {result.MeasuredIterations} (warmup: {result.WarmupIterations})";
    }

    private static OutlierMode NormalizeOutlierMode(OutlierMode mode)
    {
        return mode is OutlierMode.None
            or OutlierMode.RemoveTop5Percent
            or OutlierMode.RemoveTopAndBottom5Percent
            or OutlierMode.IqrFence
            ? mode
            : OutlierMode.RemoveTop5Percent;
    }
}
