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

        // The same gate the attribute pattern uses, so an assertion here and a gate there cannot
        // disagree about the same numbers. There is no reference method on this path, so the ratio
        // is against the calibration body; the isolation rules still apply.
        violations.AddRange(PerformanceGate.Evaluate(result, rawSamples, null, null, options).Violations);

        return violations;
    }

    private static string BuildFailureMessage(BenchmarkResult result, IReadOnlyList<string> violations)
    {
        return $"Performance assertions failed for '{result.Name}'.{Environment.NewLine}" +
               $"{string.Join(Environment.NewLine, violations)}{Environment.NewLine}{Environment.NewLine}" +
               MetricsFormatter.Format(result);
    }
}
