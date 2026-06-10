using NBenchmark.Integration.Abstractions;
using NUnit.Framework;

namespace NBenchmark.Integration.NUnit;

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
        var result = Benchmark.Run(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken);
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
        var result = Benchmark.Run(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken);
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

        var result = await Benchmark.RunAsync(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken)
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

        var result = await Benchmark.RunAsync(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken)
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
               MetricsFormatter.Format(result);
    }
}
