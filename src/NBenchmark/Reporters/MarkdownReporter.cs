using System.Text;

namespace NBenchmark.Reporters;

public sealed class MarkdownReporter : IReporter
{
    private static int _fileCounter;
    private readonly string _outputDirectory;
    private readonly string? _name;

    public MarkdownReporter(string outputDirectory = ".", string? name = null, ReportDetail detail = ReportDetail.Simple)
    {
        _outputDirectory = PathValidation.ValidateOutputPath(outputDirectory);
        _name = name;
        Detail = detail;
    }

    public string Name => "markdown";

    public ReportDetail Detail { get; set; }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var fileName = _name
                       ?? $"benchmark-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _fileCounter):D3}.md";

        var filePath = Path.Combine(_outputDirectory, fileName);

        var sb = new StringBuilder();

        sb.AppendLine("## Benchmark Results");
        sb.AppendLine();

        var table = BenchmarkTable.Build(results);

        if (table.Rows.All(r => r.Errored))
        {
            sb.AppendLine("_All benchmarks errored - no results to display._");
            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
            return;
        }

        sb.AppendLine($"_Run at {table.RunAtUtc} UTC - "
                      + $"{table.WarmupIterations} warmup / "
                      + $"{table.MeasuredIterations} measured_");

        sb.AppendLine();

        sb.AppendLine("| Benchmark | Median | Mean | Error | StdDev | P95 | P99 | Ratio | Sig | Alloc/op |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        var detailedRows = Detail == ReportDetail.Advanced
            ? new List<(string Name, string Block)>()
            : null;

        foreach (var row in table.Rows)
        {
            var error = row.Errored
                ? "-"
                : $"±{BenchmarkFormatter.FormatNs(row.MarginOfError)} ({row.MarginPercent:F2}%)";

            var sigLabel = string.IsNullOrEmpty(row.SignificanceLabel) ? "-" : row.SignificanceLabel;
            var ratio = row.Errored || double.IsNaN(row.Ratio) ? "-" : $"{row.Ratio:F2}x";

            sb.AppendLine(
                $"| {row.Name} " +
                $"| {BenchmarkFormatter.FormatNs(row.Median)} " +
                $"| {BenchmarkFormatter.FormatNs(row.Mean)} " +
                $"| {error} " +
                $"| {BenchmarkFormatter.FormatNs(row.StandardDeviation)} " +
                $"| {BenchmarkFormatter.FormatNs(row.P95)} " +
                $"| {BenchmarkFormatter.FormatNs(row.P99)} " +
                $"| {ratio} " +
                $"| {sigLabel} " +
                $"| {(row.MeanAllocatedBytes.HasValue ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value) : "-")} |"
            );

            if (Detail == ReportDetail.Advanced && !row.Errored)
            {
                var statsBlock = BenchmarkTable.RenderStatsBlock(row, Detail);
                if (!string.IsNullOrEmpty(statsBlock))
                    detailedRows!.Add((row.Name, statsBlock));
            }
        }

        if (detailedRows is not null && detailedRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Per-benchmark details");
            sb.AppendLine();

            foreach (var (name, block) in detailedRows)
            {
                sb.AppendLine($"#### {name}");
                sb.AppendLine();

                foreach (var line in block.Split('\n'))
                    sb.AppendLine($"- {line}");

                sb.AppendLine();
            }
        }

        var confidencePct = table.ConfidenceLevel * 100;
        sb.AppendLine();
        sb.AppendLine($"_Error = ±{confidencePct:0.#}% confidence interval half-width on the mean._");

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}
