namespace NBenchmark.Reporters.Console;

/// <summary>
///     Rich-console rendering for a single <see cref="BenchmarkResult" />.
/// </summary>
public static class ConsoleBenchmarkResultExtensions
{
    /// <summary>
    ///     Renders this result as a Spectre.Console table, the same one
    ///     <see cref="ConsoleReporter" /> writes for a whole run.
    /// </summary>
    /// <remarks>
    ///     Named for what it does differently from <c>BenchmarkResult.Print</c> in the core package,
    ///     which writes plain text. The two were <c>Print</c> and <c>PrintAsync</c>, distinguished only
    ///     by return type: with both <c>using</c> directives in scope, IntelliSense offered them side by
    ///     side on the same object with nothing to say that one was a table and one was not.
    /// </remarks>
    /// <param name="result">The result to render.</param>
    /// <returns>The same <paramref name="result" />, so this can sit in a chain.</returns>
    public static async Task<BenchmarkResult> PrintTableAsync(this BenchmarkResult result)
    {
        var reporter = new ConsoleReporter();
        await reporter.ReportAsync([result]);
        return result;
    }
}
