using System.Text;
using NBenchmark.Stats;

namespace NBenchmark.Reporters;

public sealed class MarkdownReporter : IReporter
{
    private const int BarWidth = 15;
    private static int _fileCounter;
    private readonly string? _name;
    private readonly string _outputDirectory;

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

        var tables = BenchmarkTable.BuildPerClass(results);
        var anyErroredAll = tables.All(t => t.Rows.All(r => r.Errored));

        if (anyErroredAll)
        {
            sb.AppendLine("_All benchmarks errored - no results to display._");
            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
            return;
        }

        sb.AppendLine(
            $"> **{tables[0].RunAtUtc} UTC** · {tables[0].WarmupIterations} warmup · {tables[0].MeasuredIterations} measured · {tables[0].Profile.ToString().ToLowerInvariant()} profile");

        sb.AppendLine();

        foreach (var table in tables)
        {
            var className = table.Rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.ClassName))?.ClassName;

            if (tables.Count > 1)
            {
                sb.AppendLine($"### {className ?? "Benchmarks"}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("### Comparison");
                sb.AppendLine();
            }

            RenderComparisonTable(sb, table, Detail);
            RenderTimingDetail(sb, table);
            RenderDistributionDetails(sb, table, Detail);
            RenderInterpretation(sb, table);
            RenderWarnings(sb, table);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }

    private static void RenderComparisonTable(StringBuilder sb, BenchmarkTable table, ReportDetail detail)
    {
        var successfulRows = table.Rows.Where(r => !r.Errored).ToList();
        var maxMedian = successfulRows.Count > 0 ? successfulRows.Max(r => r.Median) : 1;
        var showCategories = detail == ReportDetail.Advanced && table.Rows.Any(r => r.Categories.Count > 0);
        var showRuntime = table.Rows.Any(r => r.RuntimeMoniker.Length > 0);
        var paramNames = table.ParameterNames;

        // The comparison columns appear whenever rows were ranked against a reference - either a
        // competing benchmark in a parameter group or, for a single method swept across parameter
        // values, the fastest point in the table. They collapse to a lone Scale bar only when
        // nothing could be ranked (for example a class whose benchmarks all errored).
        var hasComparisons = table.Rows.Any(r => !r.Errored && !double.IsNaN(r.Ratio));

        var header = new StringBuilder("| | Benchmark |");

        if (showRuntime)
            header.Append(" Runtime |");

        foreach (var name in paramNames)
        {
            header.Append($" {name} |");
        }

        header.Append(" Median | Mean | Ops/s |");
        var separator = new StringBuilder("|:---:|---|");

        if (showRuntime)
            separator.Append("---:|");

        foreach (var _ in paramNames)
        {
            separator.Append("---:|");
        }

        separator.Append("---:|---:|---:|");

        if (hasComparisons)
        {
            header.Append(" Ratio | Scale | Sig | Magnitude |");
            separator.Append(":---:|---|---:|---:|");
        }
        else
        {
            header.Append(" Scale |");
            separator.Append("---|");
        }

        header.Append(" Alloc/op |");
        separator.Append("---:|");

        if (showCategories)
        {
            header.Append(" Categories |");
            separator.Append("---|");
        }

        sb.AppendLine(header.ToString());
        sb.AppendLine(separator.ToString());

        foreach (var row in table.Rows)
        {
            var baseName = paramNames.Count > 0 ? row.BaseName : row.Name;

            if (row.Errored)
            {
                var errored = new StringBuilder($"| ✗ | ~~{baseName}~~ |");

                if (showRuntime)
                    errored.Append($" {row.RuntimeMoniker} |");

                foreach (var name in paramNames)
                {
                    errored.Append($" {FormatParameterCell(row, name)} |");
                }

                errored.Append(" - | - | - |");
                errored.Append(hasComparisons ? " - | - | - | - |" : " - |");
                errored.Append(" - |");

                if (showCategories)
                    errored.Append(" - |");

                sb.AppendLine(errored.ToString());
                continue;
            }

            var nameText = row.IsBaseline
                ? $"**{baseName}** _(baseline)_"
                : baseName;

            var bar = RenderMarkdownBar(row.Median, maxMedian);

            var allocText = row.MeanAllocatedBytes.HasValue
                ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value)
                : "-";

            var opsText = BenchmarkFormatter.FormatOpsPerSecond(row.OperationsPerSecond);

            var line = new StringBuilder($"| | {nameText} |");

            if (showRuntime)
                line.Append($" {row.RuntimeMoniker} |");

            foreach (var name in paramNames)
            {
                line.Append($" {FormatParameterCell(row, name)} |");
            }

            line.Append(
                $" {BenchmarkFormatter.FormatNs(row.Median)} " +
                $"| {BenchmarkFormatter.FormatNs(row.Mean)} " +
                $"| {opsText} |");

            if (hasComparisons)
            {
                var sigIcon = row.SignificanceLabel switch
                {
                    "✓" => "✓",
                    "✗" => "✗",
                    _ => "-",
                };

                var magnitudeText = row.Effect?.Magnitude ?? "-";

                line.Append($" {FormatRatioText(row)} | {bar} | {sigIcon} | {magnitudeText} |");
            }
            else
                line.Append($" {bar} |");

            line.Append($" {allocText} |");

            if (showCategories)
            {
                var categoryText = row.Categories.Count > 0 ? string.Join(", ", row.Categories) : "-";
                line.Append($" {categoryText} |");
            }

            sb.AppendLine(line.ToString());
        }

        sb.AppendLine();
    }

    private static string FormatParameterCell(BenchmarkRow row, string parameterName)
    {
        var parameter = row.ParameterSet.FirstOrDefault(p => p.Name == parameterName);
        return parameter is null ? "-" : BenchmarkParameter.FormatValue(parameter.Value);
    }

    private static void RenderTimingDetail(StringBuilder sb, BenchmarkTable table)
    {
        var successful = table.Rows.Where(r => !r.Errored).ToList();
        var showRuntime = successful.Any(r => r.RuntimeMoniker.Length > 0);

        if (successful.Count == 0)
            return;

        var percentileKeys = successful
            .SelectMany(r => r.Percentiles)
            .Select(e => e.Percentile)
            .Where(p => p > 0.50 && p < 1.0)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        sb.AppendLine("### Precision & Tail Latency");
        sb.AppendLine();

        var header = new StringBuilder("| Benchmark |");
        var separator = new StringBuilder("|---|");

        if (showRuntime)
        {
            header.Append(" Runtime |");
            separator.Append("---:|");
        }

        header.Append(" Error (±CI) | StdDev | CV |");
        separator.Append("---:|---:|---:|");

        if (percentileKeys.Count > 0)
        {
            foreach (var percentile in percentileKeys)
            {
                header.Append($" P{BenchmarkTable.FormatPercentileKey(percentile)} |");
                separator.Append("---:|");
            }
        }

        sb.AppendLine(header.ToString());
        sb.AppendLine(separator.ToString());

        foreach (var row in successful)
        {
            var tailCells = percentileKeys
                .Select(p => row.GetPercentile(p))
                .Select(value => value.HasValue ? BenchmarkFormatter.FormatNs(value.Value) : string.Empty);

            var line = new StringBuilder($"| {row.Name} |");

            if (showRuntime)
                line.Append($" {row.RuntimeMoniker} |");

            line.Append(
                $" ±{BenchmarkFormatter.FormatNs(row.MarginOfError)} ({row.MarginPercent:F2}%) "
                + $"| {BenchmarkFormatter.FormatNs(row.StandardDeviation)} "
                + $"| {row.CoefficientOfVariationPercent:F2}% ");

            if (percentileKeys.Count > 0)
                line.Append($"| {string.Join(" | ", tailCells)} |");
            else
                line.Append("|");

            sb.AppendLine(line.ToString());
        }

        sb.AppendLine();
    }

    private static void RenderDistributionDetails(StringBuilder sb, BenchmarkTable table, ReportDetail detail)
    {
        if (detail != ReportDetail.Advanced)
            return;

        sb.AppendLine("### Distribution Details");
        sb.AppendLine();

        foreach (var row in table.Rows.Where(r => !r.Errored))
        {
            var statsBlock = BenchmarkTable.RenderStatsBlock(row, detail);

            if (string.IsNullOrEmpty(statsBlock))
                continue;

            sb.AppendLine("<details>");
            sb.AppendLine($"<summary><strong>{row.Name}</strong></summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(statsBlock);

            if (row.AutoTune is { } diagnostic)
                sb.AppendLine(BenchmarkTable.FormatAutoTuneSummary(diagnostic));

            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void RenderInterpretation(StringBuilder sb, BenchmarkTable table)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### Interpretation");
        sb.AppendLine();

        var hasMultipleRuntimes = table.Rows
            .Where(r => !r.Errored)
            .Select(r => r.RuntimeMoniker)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1;

        if (hasMultipleRuntimes)
        {
            sb.AppendLine("**Omnibus**: runtime-scoped in multi-runtime runs; combined summary omitted.");
            sb.AppendLine();
        }
        else if (table.Omnibus is { } omnibus)
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
        else
        {
            sb.AppendLine("**Omnibus**: not run (fewer than 3 comparable groups).");
            sb.AppendLine();
        }

        var testName = hasMultipleRuntimes ? table.SignificanceTestName : table.Omnibus?.TestName ?? table.SignificanceTestName;

        sb.AppendLine($"- Significance: {testName} (p < {table.SignificanceLevel:0.###})");
        sb.AppendLine($"- Outliers: {table.OutlierDetector}");
        sb.AppendLine($"- Effect metric: {GetEffectMetricSummary(table.Rows)}");
        sb.AppendLine();
    }

    private static void RenderWarnings(StringBuilder sb, BenchmarkTable table)
    {
        var warnings = table.Rows
            .Where(r => !r.Errored && r.Warnings.Count > 0)
            .ToList();

        if (warnings.Count == 0)
            return;

        sb.AppendLine("### Warnings");
        sb.AppendLine();

        foreach (var row in warnings)
        {
            foreach (var warning in row.Warnings)
            {
                sb.AppendLine($"- **{row.Name}**: {warning}");
            }
        }

        sb.AppendLine();
    }

    private static string RenderMarkdownBar(double value, double max)
    {
        if (max <= 0)
            return "";

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

    private static string GetEffectMetricSummary(IReadOnlyList<BenchmarkRow> rows)
    {
        var metrics = rows
            .Where(r => !r.Errored)
            .Select(r => r.Effect?.Metric)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (metrics.Count == 0)
            return "not reported by active significance strategy";

        if (metrics.Count == 1)
        {
            if (string.Equals(metrics[0], EffectMetrics.CliffsDelta, StringComparison.Ordinal))
                return "Cliff's δ (Romano neg/small/med/large labels)";

            return $"{metrics[0]} (strategy-defined labels)";
        }

        return $"mixed metrics ({string.Join(", ", metrics)})";
    }
}
