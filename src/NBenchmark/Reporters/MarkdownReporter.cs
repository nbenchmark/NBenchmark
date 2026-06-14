using System.Text;

namespace NBenchmark.Reporters;

public sealed class MarkdownReporter : IReporter
{
    private static int _fileCounter;
    private readonly string _outputDirectory;
    private readonly string? _name;

    private const int BarWidth = 15;

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

        sb.AppendLine($"> **{table.RunAtUtc} UTC** · {table.WarmupIterations} warmup · {table.MeasuredIterations} measured · Outliers: {table.OutlierDetector}");
        sb.AppendLine();

        // Primary comparison table
        var successfulRows = table.Rows.Where(r => !r.Errored).ToList();
        var maxMedian = successfulRows.Count > 0 ? successfulRows.Max(r => r.Median) : 1;

        sb.AppendLine("### Comparison");
        sb.AppendLine();
        sb.AppendLine("| | Benchmark | Median | Mean | Ratio | Scale | Sig | Alloc/op |");
        sb.AppendLine("|:---:|---|---:|---:|:---:|---|---:|---:|");

        foreach (var row in table.Rows)
        {
            if (row.Errored)
            {
                sb.AppendLine($"| ✗ | ~~{row.Name}~~ | - | - | - | - | - | - |");
                continue;
            }

            var sigIcon = row.SignificanceLabel switch
            {
                "✓" => "✓",
                "✗" => "✗",
                _ => "-",
            };

            var nameText = row.IsBaseline
                ? $"**{row.Name}** _(baseline)_"
                : row.Name;

            var ratioText = FormatRatioText(row);
            var bar = RenderMarkdownBar(row.Median, maxMedian);
            var allocText = row.MeanAllocatedBytes.HasValue
                ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value)
                : "-";

            sb.AppendLine(
                $"| " +
                $"| {nameText} " +
                $"| {BenchmarkFormatter.FormatNs(row.Median)} " +
                $"| {BenchmarkFormatter.FormatNs(row.Mean)} " +
                $"| {ratioText} " +
                $"| {bar} " +
                $"| {sigIcon} " +
                $"| {allocText} |"
            );
        }

        sb.AppendLine();

        // Timing detail table
        sb.AppendLine("### Precision & Tail Latency");
        sb.AppendLine();
        sb.AppendLine("| Benchmark | Error (±CI) | StdDev | CV | P95 | P99 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");

        foreach (var row in table.Rows.Where(r => !r.Errored))
        {
            sb.AppendLine(
                $"| {row.Name} " +
                $"| ±{BenchmarkFormatter.FormatNs(row.MarginOfError)} ({row.MarginPercent:F2}%) " +
                $"| {BenchmarkFormatter.FormatNs(row.StandardDeviation)} " +
                $"| {row.CoefficientOfVariationPercent:F2}% " +
                $"| {BenchmarkFormatter.FormatNs(row.P95)} " +
                $"| {BenchmarkFormatter.FormatNs(row.P99)} |"
            );
        }

        if (Detail == ReportDetail.Advanced)
        {
            sb.AppendLine();
            sb.AppendLine("### Distribution Details");
            sb.AppendLine();

            foreach (var row in table.Rows.Where(r => !r.Errored))
            {
                var statsBlock = BenchmarkTable.RenderStatsBlock(row, Detail);
                if (string.IsNullOrEmpty(statsBlock)) continue;

                sb.AppendLine($"<details>");
                sb.AppendLine($"<summary><strong>{row.Name}</strong></summary>");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(statsBlock);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (table.Omnibus is { } omnibus)
        {
            var verdict = omnibus.Verdict switch
            {
                SignificanceVerdict.Significant => "**significant**",
                SignificanceVerdict.NotSignificant => "not significant",
                _ => "not tested",
            };

            sb.AppendLine(
                $"**Omnibus ({omnibus.TestName})** across {omnibus.GroupCount} groups: "
                + $"H({omnibus.DegreesOfFreedom}) = {omnibus.Statistic:F2}, p = {FormatP(omnibus.PValue)} → {verdict} "
                + $"(α = {table.SignificanceLevel:0.###}).");
            sb.AppendLine();
        }

        var testName = table.Omnibus?.TestName ?? table.SignificanceTestName;
        sb.AppendLine($"_{results.Count} benchmark(s) · {table.TotalDuration.TotalSeconds:F1}s total · {testName} (p < {table.SignificanceLevel:0.###}) · CI {table.ConfidenceLevel * 100:0.#}%_");

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }

    private static string RenderMarkdownBar(double value, double max)
    {
        if (max <= 0) return "";
        var filled = (int)Math.Round(value / max * BarWidth);
        filled = Math.Clamp(filled, 1, BarWidth);
        var empty = BarWidth - filled;
        return $"`{new string('█', filled)}{new string('░', empty)}`";
    }

    private static string FormatRatioText(BenchmarkRow row)
    {
        if (double.IsNaN(row.Ratio))
            return "-";

        if (row.IsBaseline)
            return "_baseline_";

        return $"**{row.Ratio:F2}x**";
    }

    private static string FormatP(double p) => p < 0.001 ? "<0.001" : p.ToString("0.###");
}
