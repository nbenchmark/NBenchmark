using NBenchmark.Reporters;

namespace NBenchmark.Tests;

internal sealed class CapturingAutoReporter(string name) : IReporter
{
    public List<BenchmarkResult> Results { get; } = [];
    public int CallCount { get; private set; }
    public string Name => name;

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Results.AddRange(results);
        return Task.CompletedTask;
    }
}

internal sealed class CountingAutoReporter(string name, Action onCalled, Action<IReadOnlyList<BenchmarkResult>>? onResults = null) : IReporter
{
    public int CallCount { get; private set; }
    public string Name => name;

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default)
    {
        CallCount++;
        onCalled();
        onResults?.Invoke(results);
        return Task.CompletedTask;
    }
}

internal sealed class OrderTrackingReporter(string name, List<string> order) : IReporter
{
    public string Name => name;

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default)
    {
        order.Add(name);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingAutoReporter(string name) : IReporter
{
    public int CallCount { get; private set; }
    public string Name => name;

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default)
    {
        CallCount++;
        throw new InvalidOperationException("Acknowledged auto-attached reporter failure");
    }
}
