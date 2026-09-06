using NBenchmark.Reporters;
using System.Diagnostics.CodeAnalysis;

namespace NBenchmark;

public static class BenchmarkResultExtensions
{
    public static BenchmarkResult Print(this BenchmarkResult result, ReportDetail detail = ReportDetail.Simple)
    {
        Console.WriteLine();

        if (result.Errored)
        {
            Console.WriteLine($"  ✗ {result.Name}: ERROR");
            Console.WriteLine($"    {result.ErrorMessage}");
            Console.WriteLine();
            return result;
        }

        var allocText = result.AllocatedBytesMean.HasValue
            ? $"  Alloc/op: {BenchmarkFormatter.FormatBytes(result.AllocatedBytesMean.Value)}"
            : "";

        // Header
        Console.WriteLine($"  ┌─ {result.Name} ─────────────────────────────────────");
        Console.WriteLine("  │");

        if (detail == ReportDetail.Simple)
        {
            Console.WriteLine(
                $"  │  Median: {BenchmarkFormatter.FormatNs(result.MedianNs),-14} Ops/s: {BenchmarkFormatter.FormatOpsPerSecond(result.OperationsPerSecond)}");

            if (!string.IsNullOrEmpty(allocText))
                Console.WriteLine($"  │{allocText}");
        }
        else
        {
            Console.WriteLine($"  │  Median: {BenchmarkFormatter.FormatNs(result.MedianNs),-14} Mean: {BenchmarkFormatter.FormatNs(result.MeanNs)}");

            Console.WriteLine(
                $"  │  Ops/s:  {BenchmarkFormatter.FormatOpsPerSecond(result.OperationsPerSecond),-14} Median ops/s: {BenchmarkFormatter.FormatOpsPerSecond(result.MedianOperationsPerSecond)}");

            var percentileSummary = string.Join("  ", result.Percentiles
                .Where(e => e.Percentile > 0.50 && e.Percentile < 1.0)
                .Select(e =>
                {
                    var label = BenchmarkTable.FormatPercentileKey(e.Percentile);
                    return $"P{label}: {BenchmarkFormatter.FormatNs(e.Value)}";
                }));

            if (percentileSummary.Length > 0)
                Console.WriteLine($"  │  {percentileSummary}");

            Console.WriteLine($"  │  StdDev: {BenchmarkFormatter.FormatNs(result.StandardDeviationNs),-14} CV:   {result.CoefficientOfVariationPercent:F2}%");

            if (result.MarginOfErrorNs > 0)
            {
                Console.WriteLine($"  │  Error:  ±{BenchmarkFormatter.FormatNs(result.MarginOfErrorNs)} ({result.MarginOfErrorPercent:F2}% of mean)");

                Console.WriteLine(
                    $"  │  CI:     [{BenchmarkFormatter.FormatNs(result.ConfidenceIntervalLowerNs)} … {BenchmarkFormatter.FormatNs(result.ConfidenceIntervalUpperNs)}] ({result.ConfidenceLevel * 100:0.#}%)");
            }

            if (!string.IsNullOrEmpty(allocText))
                Console.WriteLine($"  │{allocText}");
        }

        if (detail == ReportDetail.Advanced)
        {
            Console.WriteLine("  │");
            Console.WriteLine($"  │  Samples: {result.SampleCount} measured (warmup: {result.WarmupSamples}, outliers removed: {result.OutliersRemoved})");

            Console.WriteLine(
                $"  │  Range:   {BenchmarkFormatter.FormatNs(result.RangeNs)} ({BenchmarkFormatter.FormatNs(result.MinNs)} → {BenchmarkFormatter.FormatNs(result.MaxNs)})");

            Console.WriteLine($"  │  Q1:     {BenchmarkFormatter.FormatNs(result.Q1Ns),-14} Q3:     {BenchmarkFormatter.FormatNs(result.Q3Ns)}");
            Console.WriteLine($"  │  IQR:    {BenchmarkFormatter.FormatNs(result.InterquartileRangeNs)}");

            if (result.LowerFenceNs is not null && result.UpperFenceNs is not null)
            {
                Console.WriteLine(
                    $"  │  Fences: [{BenchmarkFormatter.FormatNs(result.LowerFenceNs.Value)} … {BenchmarkFormatter.FormatNs(result.UpperFenceNs.Value)}]");
            }

            Console.WriteLine($"  │  Skew:   {result.Skewness:F4,-14} Kurt: {result.Kurtosis:F4}");
            Console.WriteLine($"  │  MAD:    {BenchmarkFormatter.FormatNs(result.MedianAbsoluteDeviationNs)}");

            if (result.AllocatedBytesMedian is not null)
            {
                Console.WriteLine("  │");
                Console.WriteLine($"  │  Alloc Median: {BenchmarkFormatter.FormatBytes(result.AllocatedBytesMedian.Value)}");
                Console.WriteLine($"  │  Alloc P95:    {BenchmarkFormatter.FormatBytes(result.AllocatedBytesP95 ?? 0)}");
                Console.WriteLine($"  │  Alloc Max:       {BenchmarkFormatter.FormatBytes(result.AllocatedBytesMax ?? 0)}");
            }
        }

        // Where the number came from, on the one output path that showed no provenance at all.
        // Print() is what the README leads with and what Single mode returns you to, so a benchmark
        // that quietly fell back to this process reported a tight interval with nothing anywhere
        // saying it was measured under the host's JIT tiering rather than a chosen configuration.
        Console.WriteLine("  │");

        Console.WriteLine(
            result.IsolationStatus.IsIsolated()
                ? $"  │  Measured in an isolated worker under '{result.RuntimeProfileName}'."
                : $"  │  Measured in this process ({result.IsolationStatus.ToLabel()}) under "
                  + $"'{result.RuntimeProfileName}'.");

        if (result.IsolationStatus.ToRemedy() is { } isolationRemedy)
            Console.WriteLine($"  │  To isolate it: {isolationRemedy}.");

        Console.WriteLine("  │");
        Console.WriteLine("  └─────────────────────────────────────────────────");
        Console.WriteLine();
        return result;
    }

    /// <summary>
    ///     Writes this result to a Markdown file and returns the path written.
    /// </summary>
    /// <remarks>
    ///     Named <c>Save</c> rather than <c>To</c>: <c>To*</c> conventionally converts and returns the
    ///     conversion, and this writes a file. It also used to return the result it was handed, which
    ///     said nothing about where the file went - the one thing a caller has to know next.
    /// </remarks>
    /// <param name="result">The result to write.</param>
    /// <param name="outputDir">Directory to write into. Created if it does not exist.</param>
    /// <param name="fileName">File name to use, or <c>null</c> for a generated timestamped one.</param>
    public static async Task<string> SaveMarkdownAsync(
        this BenchmarkResult result, string outputDir = ".", string? fileName = null)
    {
        var reporter = new MarkdownReporter(outputDir, fileName);
        await reporter.ReportAsync([result], ReportContext.Default);
        return reporter.LastWrittenPath!;
    }

    /// <inheritdoc cref="SaveMarkdownAsync" />
    [RequiresUnreferencedCode("Writes the report with the reflection-based JSON serializer.")]
    [RequiresDynamicCode("Writes the report with the reflection-based JSON serializer.")]
    public static async Task<string> SaveJsonAsync(
        this BenchmarkResult result, string outputDir = ".", string? fileName = null)
    {
        var reporter = new JsonReporter(outputDir, fileName);
        await reporter.ReportAsync([result], ReportContext.Default);
        return reporter.LastWrittenPath!;
    }

    /// <inheritdoc cref="SaveMarkdownAsync" />
    public static async Task<string> SaveCsvAsync(
        this BenchmarkResult result, string outputDir = ".", string? fileName = null)
    {
        var reporter = new CsvReporter(outputDir, fileName);
        await reporter.ReportAsync([result], ReportContext.Default);
        return reporter.LastWrittenPath!;
    }
}
