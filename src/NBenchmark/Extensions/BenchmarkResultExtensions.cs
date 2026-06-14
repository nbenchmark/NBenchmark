using NBenchmark.Reporters;

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

        var allocText = result.MeanAllocatedBytes.HasValue
            ? $"  Alloc/op: {BenchmarkFormatter.FormatBytes(result.MeanAllocatedBytes.Value)}"
            : "";

        // Header
        Console.WriteLine($"  ┌─ {result.Name} ─────────────────────────────────────");
        Console.WriteLine("  │");
        Console.WriteLine($"  │  Median: {BenchmarkFormatter.FormatNs(result.Median),-14} Mean: {BenchmarkFormatter.FormatNs(result.Mean)}");
        Console.WriteLine($"  │  P95:    {BenchmarkFormatter.FormatNs(result.P95),-14} P99:  {BenchmarkFormatter.FormatNs(result.P99)}");
        Console.WriteLine($"  │  StdDev: {BenchmarkFormatter.FormatNs(result.StandardDeviation),-14} CV:   {result.CoefficientOfVariationPercent:F2}%");

        if (result.MarginOfError > 0)
        {
            Console.WriteLine($"  │  Error:  ±{BenchmarkFormatter.FormatNs(result.MarginOfError)} ({result.MarginPercent:F2}% of Mean)");

            Console.WriteLine(
                $"  │  CI:     [{BenchmarkFormatter.FormatNs(result.ConfidenceIntervalLower)} … {BenchmarkFormatter.FormatNs(result.ConfidenceIntervalUpper)}] ({result.ConfidenceLevel * 100:0.#}%)");
        }

        if (!string.IsNullOrEmpty(allocText))
            Console.WriteLine($"  │{allocText}");

        if (detail == ReportDetail.Advanced)
        {
            Console.WriteLine("  │");
            Console.WriteLine($"  │  N:      {result.N} samples (warmup: {result.WarmupIterations}, outliers removed: {result.OutliersRemoved})");

            Console.WriteLine(
                $"  │  Range:  {BenchmarkFormatter.FormatNs(result.Range)} ({BenchmarkFormatter.FormatNs(result.Min)} → {BenchmarkFormatter.FormatNs(result.Max)})");

            Console.WriteLine($"  │  Q1:     {BenchmarkFormatter.FormatNs(result.Q1),-14} Q3:   {BenchmarkFormatter.FormatNs(result.Q3)}");
            Console.WriteLine($"  │  IQR:    {BenchmarkFormatter.FormatNs(result.InterquartileRange)}");

            if (result.LowerFence is not null && result.UpperFence is not null)
                Console.WriteLine(
                    $"  │  Fences: [{BenchmarkFormatter.FormatNs(result.LowerFence.Value)} … {BenchmarkFormatter.FormatNs(result.UpperFence.Value)}]");

            Console.WriteLine($"  │  Skew:   {result.Skewness:F4,-14} Kurt: {result.Kurtosis:F4}");
            Console.WriteLine($"  │  MAD:    {BenchmarkFormatter.FormatNs(result.Mad)}");

            if (result.AllocMedian is not null)
            {
                Console.WriteLine("  │");
                Console.WriteLine($"  │  Alloc Median: {BenchmarkFormatter.FormatBytes(result.AllocMedian.Value)}");
                Console.WriteLine($"  │  Alloc P95:    {BenchmarkFormatter.FormatBytes(result.AllocP95 ?? 0)}");
                Console.WriteLine($"  │  Alloc Max:    {BenchmarkFormatter.FormatBytes(result.AllocMax ?? 0)}");
            }
        }

        Console.WriteLine("  │");
        Console.WriteLine("  └─────────────────────────────────────────────────");
        Console.WriteLine();
        return result;
    }

    public static async Task<BenchmarkResult> ToMarkdownAsync(this BenchmarkResult result, string outputDir = ".", string? fileName = null)
    {
        var reporter = new MarkdownReporter(outputDir, fileName);
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToJsonAsync(this BenchmarkResult result, string outputDir = ".", string? fileName = null)
    {
        var reporter = new JsonReporter(outputDir, fileName);
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToCsvAsync(this BenchmarkResult result, string outputDir = ".", string? fileName = null)
    {
        var reporter = new CsvReporter(outputDir, fileName);
        await reporter.ReportAsync([result]);
        return result;
    }
}
