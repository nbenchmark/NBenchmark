using NBenchmark;
using NBenchmark.Engine;

namespace NBenchmark;

public static class Benchmark
{
    public static BenchmarkResult Run(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasurementEngine.MeasureSync(name, action, options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    public static BenchmarkResult Run<T>(
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

    public static async Task<BenchmarkResult> RunAsync(
        Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = await MeasurementEngine.MeasureAsync(name, action, options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    public static async Task<BenchmarkResult> RunAsync<T>(
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


}