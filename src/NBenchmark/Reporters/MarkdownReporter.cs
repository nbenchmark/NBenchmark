using System.Text;
using NBenchmark;

namespace NBenchmark.Reporters;

public sealed class MarkdownReporter : IReporter
{
    private readonly string _outputPath;

    public MarkdownReporter(string outputPath = "benchmark-results.md")
    {
        if (outputPath == "benchmark-results.md")
            outputPath = $"benchmark-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md";
        _outputPath = PathValidation.ValidateOutputPath(outputPath);
    }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Benchmark Results");
        sb.AppendLine();

        var successful = results.Where(r => !r.Errored).ToList();
        if (successful.Count == 0)
        {
            sb.AppendLine("_All benchmarks errored — no results to display._");
            await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
            return;
        }

        var headerSource = successful[0];
        sb.AppendLine($"_Run at {headerSource.RunAt:yyyy-MM-dd HH:mm:ss} UTC — "
                    + $"{headerSource.WarmupIterations} warmup / "
                    + $"{headerSource.MeasuredIterations} measured_");
        sb.AppendLine();

        var multiBenchmark = results.Count > 1;
        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                    ?? successful.MinBy(r => r.Median)!;

        sb.AppendLine("| Benchmark | Median | Mean | Error | StdDev | P95 | P99 | Ratio | Sig | Alloc/op |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var result in results.OrderBy(r => r.Median))
        {
            var ratio = result.Errored ? double.NaN : result.Median / baseline.Median;
            var sig = result.Errored || !multiBenchmark || result.IsBaseline || !result.IsSignificant.HasValue
                ? "-"
                : result.IsSignificant.Value ? "✓" : "~";

            var error = result.Errored
                ? "-"
                : $"±{BenchmarkFormatter.FormatNs(result.MarginOfError)}";

            sb.AppendLine(
                $"| {result.Name} " +
                $"| {BenchmarkFormatter.FormatNs(result.Median)} " +
                $"| {BenchmarkFormatter.FormatNs(result.Mean)} " +
                $"| {error} " +
                $"| {BenchmarkFormatter.FormatNs(result.StandardDeviation)} " +
                $"| {BenchmarkFormatter.FormatNs(result.P95)} " +
                $"| {BenchmarkFormatter.FormatNs(result.P99)} " +
                $"| {(result.Errored ? "-" : $"{ratio:F2}x")} " +
                $"| {sig} " +
                $"| {(result.MeanAllocatedBytes.HasValue ? BenchmarkFormatter.FormatBytes(result.MeanAllocatedBytes.Value) : "-")} |"
            );
        }

        var confidencePct = successful[0].ConfidenceLevel * 100;
        sb.AppendLine();
        sb.AppendLine($"_Error = ±{confidencePct:0.#}% confidence interval half-width on the mean._");

        await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
    }
}
