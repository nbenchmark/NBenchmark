namespace NBenchmark.Reporters;

public interface IReporter
{
    public string Name { get; }

    /// <summary>
    ///     Reports <paramref name="results" /> at the detail level, and to the destination,
    ///     <paramref name="context" /> describes.
    /// </summary>
    public Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        ReportContext context,
        CancellationToken cancellationToken = default);
}
