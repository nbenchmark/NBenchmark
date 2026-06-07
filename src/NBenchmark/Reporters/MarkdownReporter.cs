using System.Text;

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

    public string Name => "markdown";

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Benchmark Results");
        sb.AppendLine();

        var table = BenchmarkTable.Build(results);

        if (table.Rows.All(r => r.Errored))
        {
            sb.AppendLine("_All benchmarks errored — no results to display._");
            await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
            return;
        }

        sb.AppendLine($"_Run at {table.RunAtUtc} UTC — "
                      + $"{table.WarmupIterations} warmup / "
                      + $"{table.MeasuredIterations} measured_");

        sb.AppendLine();

        sb.AppendLine("| Benchmark | Median | Mean | Error | StdDev | P95 | P99 | Ratio | Sig | Alloc/op |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var row in table.Rows)
        {
            var error = row.Errored
                ? "-"
                : $"±{BenchmarkFormatter.FormatNs(row.MarginOfError)}";

            var sigLabel = string.IsNullOrEmpty(row.SignificanceLabel) ? "-" : row.SignificanceLabel;

            sb.AppendLine(
                $"| {row.Name} " +
                $"| {BenchmarkFormatter.FormatNs(row.Median)} " +
                $"| {BenchmarkFormatter.FormatNs(row.Mean)} " +
                $"| {error} " +
                $"| {BenchmarkFormatter.FormatNs(row.StandardDeviation)} " +
                $"| {BenchmarkFormatter.FormatNs(row.P95)} " +
                $"| {BenchmarkFormatter.FormatNs(row.P99)} " +
                $"| {(row.Errored ? "-" : $"{row.Ratio:F2}x")} " +
                $"| {sigLabel} " +
                $"| {(row.MeanAllocatedBytes.HasValue ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value) : "-")} |"
            );
        }

        var confidencePct = table.ConfidenceLevel * 100;
        sb.AppendLine();
        sb.AppendLine($"_Error = ±{confidencePct:0.#}% confidence interval half-width on the mean._");

        await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
    }
}