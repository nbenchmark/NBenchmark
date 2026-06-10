namespace NBenchmark;

public interface IBenchmarkProgress
{
    Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total);

    Task OnWarmupStarting(string name, int totalWarmupIterations);

    Task OnWarmupCompleted(string name);

    Task OnBenchmarkStarting(string name, int index, int total);

    Task OnBenchmarkCompleted(BenchmarkResult result);

    Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results);
}

public sealed class NullBenchmarkProgress : IBenchmarkProgress
{
    public static readonly NullBenchmarkProgress Instance = new();

    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
    {
        return Task.CompletedTask;
    }

    public Task OnWarmupStarting(string name, int totalWarmupIterations)
    {
        return Task.CompletedTask;
    }

    public Task OnWarmupCompleted(string name)
    {
        return Task.CompletedTask;
    }

    public Task OnBenchmarkStarting(string name, int index, int total)
    {
        return Task.CompletedTask;
    }

    public Task OnBenchmarkCompleted(BenchmarkResult result)
    {
        return Task.CompletedTask;
    }

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
    {
        return Task.CompletedTask;
    }
}