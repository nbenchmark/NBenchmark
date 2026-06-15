using NBenchmark.Engine;

namespace NBenchmark;

/// <summary>
///     Quick mode entry point: measure a single piece of code. The four overloads
///     are thin adapters on top of <see cref="BenchmarkRunner" />.
/// </summary>
public static class Benchmark
{
    public static BenchmarkResult Run(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static BenchmarkResult Run<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static async Task<BenchmarkResult> RunAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        var outcome = await BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> RunAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        var outcome = await BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public static MeasurementOutcome RunRaw(Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static MeasurementOutcome RunRaw<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> RunRawAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> RunRawAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }
}
