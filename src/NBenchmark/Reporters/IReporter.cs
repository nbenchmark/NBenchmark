namespace NBenchmark.Reporters;

/// <summary>
///     A pluggable output sink for a completed run's results - console, file, or a custom
///     destination. Register one with <c>--reporter &lt;name&gt;</c> via
///     <see cref="ReporterRegistry.Register" />, or pass an instance directly.
/// </summary>
public interface IReporter
{
    /// <summary>The reporter's name, as used to dedupe explicit and auto-attached reporters.</summary>
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
