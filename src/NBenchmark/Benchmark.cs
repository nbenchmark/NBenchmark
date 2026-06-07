using NBenchmark.Engine;

namespace NBenchmark;

/// <summary>
///     Tier 1 entry point: measure a single piece of code. The four overloads
///     are thin adapters on top of <see cref="BenchmarkRunner" />.
/// </summary>
public static class Benchmark
{
    public static BenchmarkResult Run(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static BenchmarkResult Run<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static async Task<BenchmarkResult> RunAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        var outcome = await BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> RunAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        var outcome = await BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public static MeasurementOutcome MeasureRaw(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static MeasurementOutcome MeasureRaw<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> MeasureRawAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> MeasureRawAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default };
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }
}
