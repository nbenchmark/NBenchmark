using System.Text;

namespace NBenchmark.Reporters;

public sealed class MarkdownReporter : IReporter
{
    private static int _fileCounter;

    private readonly string _outputDirectory;
    private readonly string? _fileName;

    public MarkdownReporter(string outputDirectory = ".", string? fileName = null)
    {
        _outputDirectory = PathValidation.ValidateOutputPath(outputDirectory);
        _fileName = fileName;
    }

    public string Name => "markdown";

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var fileName = _fileName
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

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}