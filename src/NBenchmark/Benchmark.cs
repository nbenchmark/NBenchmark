using NBenchmark.Engine;

namespace NBenchmark;

/// <summary>
///     Single mode entry point: measure a single piece of code. The four overloads
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
        EmitBuildConfigurationGuidanceOnce(options);
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static BenchmarkResult Run<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        EmitBuildConfigurationGuidanceOnce(options);
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken).Result;
    }

    public static async Task<BenchmarkResult> RunAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        EmitBuildConfigurationGuidanceOnce(options);
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
        EmitBuildConfigurationGuidanceOnce(options);
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
        EmitBuildConfigurationGuidanceOnce(options);
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static MeasurementOutcome RunRaw<T>(Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        EmitBuildConfigurationGuidanceOnce(options);
        return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> RunRawAsync(Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        EmitBuildConfigurationGuidanceOnce(options);
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }

    public static Task<MeasurementOutcome> RunRawAsync<T>(Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        IBenchmarkProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new RunSpec { Options = options ?? MeasurementOptions.Default, Progress = progress ?? NullBenchmarkProgress.Instance };
        EmitBuildConfigurationGuidanceOnce(options);
        return BenchmarkRunner.Instance.RunAsync(name, action, spec, cancellationToken);
    }

    /// <summary>
    ///     Emits the always-on Debug-build / debugger-attached warning once per process.
    ///     Single-method mode does not go through <see cref="EnvironmentControl.Apply" />
    ///     (which emits it for Suite and Harness mode), so the facade calls it directly.
    ///     The once-per-process guard inside <see cref="EnvironmentControl" /> prevents
    ///     double emission when <see cref="Benchmark.Run" /> is called from inside a
    ///     Suite or Harness process that already warned via <c>Apply</c>.
    /// </summary>
    private static void EmitBuildConfigurationGuidanceOnce(MeasurementOptions? options)
        => EnvironmentControl.EmitBuildConfigurationGuidance(options?.Environment);
}
