namespace NBenchmark;

public interface IBenchmarkProgress
{
    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total);

    public Task OnWarmupStarting(string name, int totalWarmupIterations);

    public Task OnWarmupCompleted(string name);

    public Task OnBenchmarkStarting(string name, int index, int total);

    public Task OnIterationCompleted(string name, int iteration, int totalIterations);

    public Task OnBenchmarkCompleted(BenchmarkResult result);

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results);
}

public sealed class NullBenchmarkProgress : IBenchmarkProgress
{
    public static readonly NullBenchmarkProgress Instance = new();

    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) => Task.CompletedTask;

    public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;

    public Task OnWarmupCompleted(string name) => Task.CompletedTask;

    public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;

    public Task OnIterationCompleted(string name, int iteration, int totalIterations) => Task.CompletedTask;

    public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Task.CompletedTask;
}
