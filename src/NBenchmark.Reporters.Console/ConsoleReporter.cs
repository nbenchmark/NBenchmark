using System.Runtime.CompilerServices;
using NBenchmark.Stats;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NBenchmark.Reporters.Console;

public sealed class ConsoleReporter : IReporter
{
    private const int BarWidth = 12;

    public ConsoleReporter(ReportDetail detail = ReportDetail.Simple)
    {
        Detail = detail;
    }

    public string Name => "console";

    public ReportDetail Detail { get; set; }

    public Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No results to display.[/]");
            return Task.CompletedTask;
        }

        var benchTable = BenchmarkTable.Build(results);

        if (benchTable.Rows.All(r => r.Errored))
        {
            foreach (var row in benchTable.Rows)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Esc(row.Name)}: {Esc(row.ErrorMessage)}[/]");
            }

            return Task.CompletedTask;
        }

        AnsiConsole.WriteLine();
        RenderHeader(benchTable);
        AnsiConsole.WriteLine();
        RenderComparisonTable(benchTable, results);
        AnsiConsole.WriteLine();
        RenderTimingDetail(benchTable);

        if (Detail == ReportDetail.Advanced)
        {
            AnsiConsole.WriteLine();
            RenderAdvancedDetails(benchTable);
        }

        AnsiConsole.WriteLine();
        RenderInterpretation(benchTable, results.Count);
        RenderWarnings(benchTable);
        AnsiConsole.WriteLine();

        return Task.CompletedTask;
    }

    private static void RenderHeader(BenchmarkTable benchTable)
    {
        var profileLabel = benchTable.Profile switch
        {
            MeasurementProfile.Independent => "independent (per-iteration GC, between-benchmark GC, no alloc tracking)",
            _ => "realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)",
        };

        var headerGrid = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap());

        headerGrid.AddRow(
            "[bold steelblue1]BENCHMARK RESULTS[/]",
            $"[grey]{benchTable.RunAtUtc} UTC[/]",
            $"[grey]{benchTable.WarmupIterations} warmup / {benchTable.MeasuredIterations} measured[/]"
        );

        headerGrid.AddRow(
            "",
            $"[grey]Profile: {profileLabel}[/]",
            ""
        );

        var panel = new Panel(headerGrid)
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.SteelBlue1)
            .Padding(1, 0);

        AnsiConsole.Write(panel);
    }

    private static void RenderComparisonTable(BenchmarkTable benchTable, IReadOnlyList<BenchmarkResult> results)
    {
        var successfulRows = benchTable.Rows.Where(r => !r.Errored).ToList();
        var maxMedian = successfulRows.Count > 0 ? successfulRows.Max(r => r.Median) : 1;

        var table = new Table()
            .Border(TableBorder.Simple)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Benchmark[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Median[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]Mean[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn("[bold]vs Baseline[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Sig[/]").Centered().NoWrap())
            .AddColumn(new TableColumn("[bold]Magnitude[/]").Centered().NoWrap())
            .AddColumn(new TableColumn("[bold]Alloc/op[/]").RightAligned().NoWrap());

        var hasDescriptions = results.Any(r => !string.IsNullOrEmpty(r.Description));

        if (hasDescriptions)
            table.AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var row in benchTable.Rows)
        {
            if (row.Errored)
            {
                var errorCols = new List<string>
                {
                    $"[red]✗ {Esc(row.Name)}[/]",
                    "[dim]-[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]", "[dim]-[/]",
                };

                if (hasDescriptions)
                    errorCols.Add("[dim]-[/]");

                table.AddRow(errorCols.ToArray());
                continue;
            }

            var (ratioText, ratioColor) = FormatRatio(row);

            var sigIcon = row.SignificanceLabel switch
            {
                "✓" => "[green]✓[/]",
                "✗" => "[red]✗[/]",
                _ => "[dim]-[/]",
            };

            var nameText = row.IsBaseline
                ? $"[bold]{Esc(row.Name)}[/] [dim italic](baseline)[/]"
                : $"[{ratioColor}]{Esc(row.Name)}[/]";

            var bar = RenderBar(row.Median, maxMedian, ratioColor);

            var ratioAndBar = row.IsBaseline
                ? $"{bar} [dim]{ratioText}[/]"
                : $"{bar} [{ratioColor}]{ratioText}[/]";

            var allocText = row.MeanAllocatedBytes.HasValue
                ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value)
                : "[dim]-[/]";

            var magnitudeText = RenderMagnitude(row.Effect);

            var rowCols = new List<string>
            {
                nameText,
                $"[bold]{BenchmarkFormatter.FormatNs(row.Median)}[/]",
                BenchmarkFormatter.FormatNs(row.Mean),
                ratioAndBar,
                sigIcon,
                magnitudeText,
                allocText,
            };

            if (hasDescriptions)
                rowCols.Add(string.IsNullOrEmpty(row.Description) ? "" : Esc(row.Description));

            table.AddRow(rowCols.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderTimingDetail(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Errored).ToList();

        if (rows.Count == 0)
            return;

        var rule = new Rule("[dim]Precision & Tail Latency[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey42)
            .AddColumn(new TableColumn("[dim]Benchmark[/]").NoWrap())
            .AddColumn(new TableColumn("[dim]Error (±CI)[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]StdDev[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]CV[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]P95[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]P99[/]").RightAligned());

        foreach (var row in rows)
        {
            var cvColor = row.CoefficientOfVariationPercent switch
            {
                <= 2.0 => "green",
                <= 5.0 => "yellow",
                _ => "red",
            };

            table.AddRow(
                $"[dim]{Esc(row.Name)}[/]",
                $"±{BenchmarkFormatter.FormatNs(row.MarginOfError)} [dim]({row.MarginPercent:F2}%)[/]",
                BenchmarkFormatter.FormatNs(row.StandardDeviation),
                $"[{cvColor}]{row.CoefficientOfVariationPercent:F2}%[/]",
                BenchmarkFormatter.FormatNs(row.P95),
                BenchmarkFormatter.FormatNs(row.P99)
            );
        }

        AnsiConsole.Write(table);
    }

    private static void RenderAdvancedDetails(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Errored).ToList();

        if (rows.Count == 0)
            return;

        var rule = new Rule("[dim]Distribution Details[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var grid = new Grid();

        foreach (var _ in rows)
        {
            grid.AddColumn(new GridColumn().PadRight(2));
        }

        // Render each benchmark's details as a panel in a horizontal grid
        var panels = new List<IRenderable>();

        foreach (var row in rows)
        {
            var statsBlock = BenchmarkTable.RenderStatsBlock(row, ReportDetail.Advanced);

            if (string.IsNullOrEmpty(statsBlock))
                continue;

            var panel = new Panel(Markup.Escape(statsBlock))
                .Header($"[bold]{Esc(row.Name)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey42)
                .Padding(1, 0)
                .Expand();

            panels.Add(panel);
        }

        if (panels.Count <= 3)
        {
            // Side-by-side panels for up to 3 benchmarks
            var columns = new Columns(panels);
            columns.Expand = true;
            AnsiConsole.Write(columns);
        }
        else
        {
            // Stack vertically for many benchmarks
            foreach (var panel in panels)
            {
                AnsiConsole.Write(panel);
                AnsiConsole.WriteLine();
            }
        }
    }

    private static void RenderWarnings(BenchmarkTable benchTable)
    {
        var warnings = benchTable.Rows
            .Where(r => !r.Errored && r.Warnings.Count > 0)
            .ToList();

        if (warnings.Count == 0)
            return;

        AnsiConsole.WriteLine();

        var rule = new Rule("[yellow]Warnings[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("yellow"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        foreach (var row in warnings)
        {
            foreach (var warning in row.Warnings)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ {Esc(row.Name)}:[/] [dim]{Esc(warning)}[/]");
            }
        }
    }

    private static void RenderInterpretation(BenchmarkTable benchTable, int count)
    {
        var rule = new Rule("[dim]Interpretation[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        if (benchTable.Omnibus is { } omnibus)
        {
            var (verdict, color) = omnibus.Verdict switch
            {
                SignificanceVerdict.Significant => ("significant", "green"),
                SignificanceVerdict.NotSignificant => ("not significant", "yellow"),
                _ => ("not tested", "dim"),
            };

            AnsiConsole.MarkupLine(
                $"[grey]Omnibus:[/] [bold]{Esc(omnibus.TestName)}[/][grey] across {omnibus.GroupCount} groups: "
                + $"H({omnibus.DegreesOfFreedom}) = {omnibus.Statistic:F2}, p = {FormatP(omnibus.PValue)} → [/]"
                + $"[{color}]{verdict}[/] [grey](α = {benchTable.SignificanceLevel:0.###})[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Omnibus:[/] [dim]not run (fewer than 3 comparable groups)[/]");
        }

        var testName = benchTable.Omnibus?.TestName ?? benchTable.SignificanceTestName;

        AnsiConsole.MarkupLine(
            $"[grey]Significance:[/] [dim]{Esc(testName)} (p < {benchTable.SignificanceLevel:0.###})[/]");

        AnsiConsole.MarkupLine($"[grey]Outliers:[/] [dim]{Esc(benchTable.OutlierDetector)}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Effect metric:[/] [dim]{Esc(GetEffectMetricSummary(benchTable.Rows))}[/]");

        AnsiConsole.MarkupLine(
            $"[dim]{count} benchmark(s) · {benchTable.TotalDuration.TotalSeconds:F1}s total · CI {benchTable.ConfidenceLevel * 100:0.#}%[/]");
    }

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

    private static string FormatP(double p) => p < 0.001 ? "<0.001" : p.ToString("0.###");

    private static string RenderMagnitude(EffectSize? effect)
    {
        if (effect is not { } value)
            return "[dim]-[/]";

        var magnitude = string.IsNullOrWhiteSpace(value.Magnitude)
            ? value.Value?.ToString("F3") ?? "-"
            : value.Magnitude;

        if (string.Equals(magnitude, "neg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(magnitude, "negligible", StringComparison.OrdinalIgnoreCase))
            return "[dim]neg[/]";

        if (string.Equals(magnitude, "small", StringComparison.OrdinalIgnoreCase))
            return "[yellow]small[/]";

        if (string.Equals(magnitude, "med", StringComparison.OrdinalIgnoreCase)
            || string.Equals(magnitude, "medium", StringComparison.OrdinalIgnoreCase))
            return "[orange1]med[/]";

        if (string.Equals(magnitude, "large", StringComparison.OrdinalIgnoreCase))
        {
            return value.Direction switch
            {
                EffectDirection.CandidateHigher => "[bold red]large[/]",
                EffectDirection.CandidateLower => "[bold green]large[/]",
                _ => "[bold]large[/]",
            };
        }

        return $"[cyan]{Esc(magnitude)}[/]";
    }

    private static string RenderBar(double value, double max, string color)
    {
        if (max <= 0)
            return "";

        var filled = (int)Math.Round(value / max * BarWidth);
        filled = Math.Clamp(filled, 1, BarWidth);
        var empty = BarWidth - filled;
        return $"[{color}]{new string('█', filled)}[/][dim]{new string('░', empty)}[/]";
    }

    private static (string Text, string Color) FormatRatio(BenchmarkRow row)
    {
        if (double.IsNaN(row.Ratio))
            return ("-", "dim");

        if (row.IsBaseline)
            return ("baseline", "dim");

        var text = $"{row.Ratio:F2}x";

        var color = row.Ratio switch
        {
            <= 1.05 => "green",
            <= 1.5 => "yellow",
            _ => "red",
        };

        return (text, color);
    }

    [ModuleInitializer]
    public static void Register() =>
        ReporterRegistry.Register(
            "console",
            "Console output (Spectre.Console table + bar chart)",
            (_, detail) => new ConsoleReporter(detail));

    private static string Esc(string? text) => Markup.Escape(text ?? "");
}
