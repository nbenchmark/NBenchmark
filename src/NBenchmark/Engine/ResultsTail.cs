using NBenchmark.Reporters;

namespace NBenchmark.Engine;

internal static class ResultsTail
{
    public static async Task ApplyAsync(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples,
        MeasurementOptions options,
        IReadOnlyList<IReporter> reporters,
        CancellationToken cancellationToken)
    {
        Significance.ApplyIfEnabled(results, rawSamples, options);

        foreach (var reporter in reporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }
    }
}
