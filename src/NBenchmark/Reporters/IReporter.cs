namespace NBenchmark.Reporters;

public interface IReporter
{
    public string Name { get; }

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
