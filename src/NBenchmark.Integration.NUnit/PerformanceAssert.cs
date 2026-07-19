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
        var outcome = Benchmark.RunRaw(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken: cancellationToken);
        Validate(outcome.Result, outcome.RawSamples, resolvedOptions);
        return outcome.Result;
    }

    public static BenchmarkResult Run<T>(
        Func<T> action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var outcome = Benchmark.RunRaw(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken: cancellationToken);
        Validate(outcome.Result, outcome.RawSamples, resolvedOptions);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> RunAsync(
        Func<Task> action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();

        var outcome = await Benchmark.RunRawAsync(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Validate(outcome.Result, outcome.RawSamples, resolvedOptions);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> RunAsync<T>(
        Func<Task<T>> action,
        PerformanceAssertionOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();

        var outcome = await Benchmark.RunRawAsync(action, MeasurementOptionsBuilder.Build(resolvedOptions), name, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Validate(outcome.Result, outcome.RawSamples, resolvedOptions);
        return outcome.Result;
    }

    public static void Validate(BenchmarkResult result, PerformanceAssertionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var violations = CollectViolations(result, [], resolvedOptions);

        if (violations.Count == 0)
            return;

        Assert.Fail(BuildFailureMessage(result, violations));
    }

    public static void Validate(BenchmarkResult result, double[] rawSamples, PerformanceAssertionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rawSamples);

        var resolvedOptions = options ?? new PerformanceAssertionOptions();
        var violations = CollectViolations(result, rawSamples, resolvedOptions);

        if (violations.Count == 0)
            return;

        Assert.Fail(BuildFailureMessage(result, violations));
    }

    private static List<string> CollectViolations(BenchmarkResult result, double[] rawSamples, PerformanceAssertionOptions options)
    {
        var violations = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ReferenceMethod))
        {
            violations.Add(
                "ReferenceMethod is not supported in the PerformanceAssert assert pattern. " +
                "Use the attribute pattern ([Performance] on a test method) to compare against a reference method, " +
                "or use calibration mode (leave ReferenceMethod null).");

            return violations;
        }

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = options.MaxMeanNs >= 0 ? options.MaxMeanNs : null,
            MaxP95Ns = options.MaxP95Ns >= 0 ? options.MaxP95Ns : null,
            MaxAllocatedBytes = options.MaxAllocatedBytes >= 0 ? options.MaxAllocatedBytes : null,
            MaxAbsoluteThresholdTolerance = options.MaxAbsoluteThresholdTolerance,
        };

        violations.AddRange(BenchmarkAssert.Validate(result, thresholds));

        if (options.MaxSlowdownRatio > 0 && !result.Errored)
        {
            var calibration = PerformanceCalibration.Run();

            violations.AddRange(RelativeComparison.Check(
                result, rawSamples, PerformanceCalibration.CreateBenchmarkResult(), calibration.Samples, options.MaxSlowdownRatio));
        }

        return violations;
    }

    private static string BuildFailureMessage(BenchmarkResult result, IReadOnlyList<string> violations)
    {
        return $"Performance assertions failed for '{result.Name}'.{Environment.NewLine}" +
               $"{string.Join(Environment.NewLine, violations)}{Environment.NewLine}{Environment.NewLine}" +
               MetricsFormatter.Format(result);
    }
}
