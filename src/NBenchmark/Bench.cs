using NBenchmark;
using NBenchmark.Engine;

namespace NBenchmark;

public static class Bench
{
    public static BenchmarkResult Time(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasurementEngine.MeasureSync(name, action, options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    public static BenchmarkResult Time<T>(
        Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasurementEngine.MeasureSync(name,
            () => ResultSink.Consume(action()),
            options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> TimeAsync(
        Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = await MeasurementEngine.MeasureAsync(name, action, options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> TimeAsync<T>(
        Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = await MeasurementEngine.MeasureAsync(
            name,
            async () => ResultSink.Consume(await action()),
            options,
            cancellationToken: cancellationToken);
        return outcome.Result;
    }

    public static MeasurementOutcome MeasureRaw(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        return MeasurementEngine.MeasureSync(name, action, options, cancellationToken: cancellationToken);
    }

    [Obsolete("Use Bench.Time() for sync benchmarks. TimeAsync(Action) is retained for backward compatibility.")]
    public static Task<BenchmarkResult> TimeAsync(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Time(action, options, name, cancellationToken));
    }

    [Obsolete("Use Bench.Time<T>() for sync benchmarks with return values.")]
    public static Task<BenchmarkResult> TimeAsync<T>(
        Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Time(action, options, name, cancellationToken));
    }
}
