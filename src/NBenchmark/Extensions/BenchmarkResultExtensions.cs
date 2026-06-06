using NBenchmark.Reporters;

namespace NBenchmark;

public static class BenchmarkResultExtensions
{
    public static BenchmarkResult Print(this BenchmarkResult result)
    {
        Console.WriteLine();

        var allocSuffix = result.MeanAllocatedBytes.HasValue
            ? $" ({BenchmarkFormatter.FormatBytes(result.MeanAllocatedBytes.Value)})"
            : "";

        Console.WriteLine($"  {result.Name}: {BenchmarkFormatter.FormatNs(result.Median)} median{allocSuffix}");
        Console.WriteLine($"    Mean: {BenchmarkFormatter.FormatNs(result.Mean)}, P95: {BenchmarkFormatter.FormatNs(result.P95)}");
        Console.WriteLine($"    StdDev: {BenchmarkFormatter.FormatNs(result.StandardDeviation)}");

        if (result.MarginOfError > 0)
        {
            Console.WriteLine(
                $"    {result.ConfidenceLevel * 100:0.#}% CI: "
                + $"{BenchmarkFormatter.FormatNs(result.ConfidenceIntervalLower)} … "
                + $"{BenchmarkFormatter.FormatNs(result.ConfidenceIntervalUpper)} "
                + $"(±{BenchmarkFormatter.FormatNs(result.MarginOfError)})");
        }

        Console.WriteLine();
        return result;
    }

    public static async Task<BenchmarkResult> ToMarkdownAsync(this BenchmarkResult result, string path = "benchmark.md")
    {
        var reporter = new MarkdownReporter(path);
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToJsonAsync(this BenchmarkResult result, string outputDir = ".")
    {
        var reporter = new JsonReporter(outputDir);
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToCsvAsync(this BenchmarkResult result, string path = "benchmark.csv")
    {
        var reporter = new CsvReporter(path);
        await reporter.ReportAsync([result]);
        return result;
    }
}