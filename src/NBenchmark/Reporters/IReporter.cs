using NBenchmark;

namespace NBenchmark.Reporters;

public interface IReporter
{
    Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
