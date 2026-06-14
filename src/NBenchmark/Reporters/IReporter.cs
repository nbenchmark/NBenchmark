namespace NBenchmark.Reporters;

public interface IReporter
{
    public string Name { get; }

    public ReportDetail Detail { get; set; }

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
