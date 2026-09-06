namespace NBenchmark;

public interface IBenchmarkProgress
{
    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total);

    /// <summary>Signals that the warmup phase is starting.</summary>
    /// <param name="totalWarmupSamples">
    ///     The planned warmup count, or a value &lt;= 0 when warmup length is auto-resolved by the
    ///     plateau rule and not known in advance. Progress UIs should treat a non-positive total as
    ///     an indeterminate phase (no percentage or ETA).
    /// </param>
    public Task OnWarmupStarting(string name, int totalWarmupSamples);

    public Task OnWarmupCompleted(string name);

    public Task OnBenchmarkStarting(string name, int index, int total);

    /// <summary>Signals that a measured sample completed.</summary>
    /// <param name="totalSamples">
    ///     The planned sample total, or a value &lt;= 0 when the count is auto-resolved (the loop
    ///     stops on a confidence-interval target) and not known in advance. Progress UIs should
    ///     treat a non-positive total as indeterminate and avoid showing a percentage or ETA.
    /// </param>
    public Task OnSampleCompleted(string name, int sample, int totalSamples);

    public Task OnBenchmarkCompleted(BenchmarkResult result);

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results);
}

public sealed class NullBenchmarkProgress : IBenchmarkProgress
{
    public static readonly NullBenchmarkProgress Instance = new();

    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) => Task.CompletedTask;

    public Task OnWarmupStarting(string name, int totalWarmupSamples) => Task.CompletedTask;

    public Task OnWarmupCompleted(string name) => Task.CompletedTask;

    public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;

    public Task OnSampleCompleted(string name, int sample, int totalSamples) => Task.CompletedTask;

    public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Task.CompletedTask;
}
