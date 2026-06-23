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

        var showCategories = Detail == ReportDetail.Advanced && results.Any(r => r.Categories.Count > 0);

        if (results.All(r => r.Errored))
        {
            foreach (var row in results)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Esc(row.Name)}: {Esc(row.ErrorMessage)}[/]");
            }

            return Task.CompletedTask;
        }

        var tables = BenchmarkTable.BuildPerClass(results);
        var firstTable = tables[0];

        if (Detail == ReportDetail.Simple)
        {
            foreach (var table in tables)
            {
                var className = BenchmarkTable.CrossClassMode
                    ? null
                    : table.Rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.ClassName))?.ClassName;

                RenderComparisonTable(table, className, Detail);
                AnsiConsole.WriteLine();
                RenderSimpleFooter(table);
                RenderWarnings(table);
            }

            AnsiConsole.WriteLine();
            return Task.CompletedTask;
        }

        AnsiConsole.WriteLine();
        RenderHeader(firstTable);

        foreach (var table in tables)
        {
            var className = BenchmarkTable.CrossClassMode
                ? null
                : table.Rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.ClassName))?.ClassName;

            RenderComparisonTable(table, className, Detail);
            AnsiConsole.WriteLine();
            RenderTimingDetail(table);
            RenderLaunchStats(table);
            AnsiConsole.WriteLine();
            RenderInterpretation(table, table.Rows.Count);
            RenderAutoTune(table);

            if (Detail == ReportDetail.Advanced)
            {
                AnsiConsole.WriteLine();
                RenderAdvancedDetails(table);
            }

            AnsiConsole.WriteLine();
            RenderWarnings(table);
        }

        AnsiConsole.WriteLine();

        return Task.CompletedTask;
    }

    private static void RenderSimpleFooter(BenchmarkTable benchTable)
    {
        var count = benchTable.Rows.Count(r => !r.Errored);
        AnsiConsole.MarkupLine(
            $"[dim]{count} benchmark(s) · {benchTable.TotalDuration.TotalSeconds:F1}s total · CI {benchTable.ConfidenceLevel * 100:0.#}%[/]");
    }

    private static void RenderHeader(BenchmarkTable benchTable)
    {
        var rule = new Rule($"[bold steelblue1]BENCHMARK RESULTS[/]  [grey]{benchTable.RunAtUtc} UTC[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("steelblue1"));

        AnsiConsole.Write(rule);
    }

    private static void RenderComparisonTable(BenchmarkTable benchTable, string? sectionClassName, ReportDetail detail)
    {
        var successfulRows = benchTable.Rows.Where(r => !r.Errored).ToList();
        var maxMedian = successfulRows.Count > 0 ? successfulRows.Max(r => r.Median) : 1;
        var showCategories = detail == ReportDetail.Advanced && benchTable.Rows.Any(r => r.Categories.Count > 0);
        var showRuntime = benchTable.Rows.Any(r => r.RuntimeMoniker.Length > 0);
        var isSimple = detail == ReportDetail.Simple;
        var showClass = BenchmarkTable.CrossClassMode && benchTable.Rows.Any(r => r.ClassName.Length > 0);

        // A ratio is present whenever a row was ranked against a reference - either a competing
        // benchmark in its parameter group or, for a single method swept across parameter values,
        // the fastest point in the table. Parametric tables use compact headers to save width.
        var hasComparisons = benchTable.Rows.Any(r => !r.Errored && !double.IsNaN(r.Ratio));
        var ratioHeader = "Ratio";
        var magnitudeHeader = "Mag";

        var table = new Table()
            .Border(TableBorder.Simple)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Benchmark[/]").NoWrap());

        if (showClass)
            table.AddColumn(new TableColumn("[bold]Class[/]").NoWrap());

        if (showRuntime)
            table.AddColumn(new TableColumn("[bold]Runtime[/]").RightAligned().NoWrap());

        foreach (var paramName in benchTable.ParameterNames)
        {
            table.AddColumn(new TableColumn($"[bold]{Esc(paramName)}[/]").RightAligned().NoWrap());
        }

        table
            .AddColumn(new TableColumn("[bold]Median[/]").RightAligned().NoWrap());

        if (!isSimple)
        {
            table.AddColumn(new TableColumn("[bold]Mean[/]").RightAligned().NoWrap());
        }

        table
            .AddColumn(new TableColumn("[bold]Ops/s[/]").RightAligned().NoWrap())
            .AddColumn(new TableColumn($"[bold]{(hasComparisons ? ratioHeader : "Scale")}[/]").NoWrap());

        if (hasComparisons)
        {
            table
                .AddColumn(new TableColumn("[bold]Sig[/]").Centered().NoWrap());

            if (!isSimple)
                table.AddColumn(new TableColumn($"[bold]{magnitudeHeader}[/]").Centered().NoWrap());
        }

        table.AddColumn(new TableColumn("[bold]Alloc/op[/]").RightAligned().NoWrap());

        if (showCategories)
            table.AddColumn(new TableColumn("[bold]Categories[/]"));

        var hasDescriptions = !isSimple && benchTable.Rows.Any(r => !string.IsNullOrEmpty(r.Description));

        if (hasDescriptions)
            table.AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var row in benchTable.Rows)
        {
            var rawName = benchTable.ParameterNames.Count > 0 ? row.BaseName : row.Name;

            var displayName = !string.IsNullOrEmpty(sectionClassName) && rawName.StartsWith(sectionClassName + ".", StringComparison.Ordinal)
                ? rawName[(sectionClassName.Length + 1)..]
                : rawName;

            if (row.Errored)
            {
                var errorCols = new List<string> { $"[red]✗ {Esc(displayName)}[/]" };

                if (showClass)
                    errorCols.Add(Esc(row.ClassName));

                if (showRuntime)
                    errorCols.Add(Esc(row.RuntimeMoniker));

                errorCols.AddRange(ParameterCells(row, benchTable.ParameterNames));
                errorCols.Add("[dim]-[/]");

                if (!isSimple)
                    errorCols.Add("[dim]-[/]");

                errorCols.AddRange(["[dim]-[/]", "[dim]-[/]"]);

                if (hasComparisons)
                {
                    errorCols.Add("[dim]-[/]");
                    if (!isSimple)
                        errorCols.Add("[dim]-[/]");
                }

                errorCols.Add("[dim]-[/]");

                if (showCategories)
                    errorCols.Add("[dim]-[/]");

                if (hasDescriptions)
                    errorCols.Add("[dim]-[/]");

                table.AddRow(errorCols.ToArray());
                continue;
            }

            var (ratioText, ratioColor) = FormatRatio(row);

            var nameText = row.IsBaseline
                ? $"[bold]{Esc(displayName)}[/] [dim italic](baseline)[/]"
                : $"[{ratioColor}]{Esc(displayName)}[/]";

            var bar = RenderBar(row.Median, maxMedian, ratioColor);

            string barCell;

            if (!hasComparisons)
                barCell = bar;
            else if (row.IsBaseline)
                barCell = $"{bar} [dim]{ratioText}[/]";
            else
                barCell = $"{bar} [{ratioColor}]{ratioText}[/]";

            var allocText = row.MeanAllocatedBytes.HasValue
                ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value)
                : "[dim]-[/]";

            var rowCols = new List<string> { nameText };

            if (showClass)
                rowCols.Add(Esc(row.ClassName));

            if (showRuntime)
                rowCols.Add(Esc(row.RuntimeMoniker));

            rowCols.AddRange(ParameterCells(row, benchTable.ParameterNames));

            rowCols.AddRange([
                $"[bold]{BenchmarkFormatter.FormatNs(row.Median)}[/]",
            ]);

            if (!isSimple)
                rowCols.Add(BenchmarkFormatter.FormatNs(row.Mean));

            rowCols.AddRange([
                BenchmarkFormatter.FormatOpsPerSecond(row.OperationsPerSecond),
                barCell,
            ]);

            if (hasComparisons)
            {
                var sigIcon = row.SignificanceLabel switch
                {
                    "✓" => "[green]✓[/]",
                    "✗" => "[red]✗[/]",
                    _ => "[dim]-[/]",
                };

                rowCols.Add(sigIcon);
                if (!isSimple)
                    rowCols.Add(RenderMagnitude(row.Effect));
            }

            rowCols.Add(allocText);

            if (showCategories)
                rowCols.Add(row.Categories.Count > 0 ? Esc(string.Join(", ", row.Categories)) : "[dim]-[/]");

            if (hasDescriptions)
                rowCols.Add(string.IsNullOrEmpty(row.Description) ? "" : Esc(row.Description));

            table.AddRow(rowCols.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static IEnumerable<string> ParameterCells(BenchmarkRow row, IReadOnlyList<string> parameterNames)
    {
        foreach (var parameterName in parameterNames)
        {
            var parameter = row.ParameterSet.FirstOrDefault(p => p.Name == parameterName);
            yield return parameter is null ? "[dim]-[/]" : Esc(BenchmarkParameter.FormatValue(parameter.Value));
        }
    }

    private static void RenderTimingDetail(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Errored).ToList();
        var showRuntime = rows.Any(r => r.RuntimeMoniker.Length > 0);

        if (rows.Count == 0)
            return;

        var rule = new Rule("[dim]Precision & Tail Latency[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        // Determine which percentile columns to show (upper tail: >P50, not Max).
        // Use the union across successful rows so errored/partial rows cannot hide columns.
        var percentileKeys = rows
            .SelectMany(r => r.Percentiles)
            .Select(e => e.Percentile)
            .Where(p => p > 0.50 && p < 1.0)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey42)
            .AddColumn(new TableColumn("[dim]Benchmark[/]").NoWrap());

        if (showRuntime)
            table.AddColumn(new TableColumn("[dim]Runtime[/]").RightAligned().NoWrap());

        table
            .AddColumn(new TableColumn("[dim]Error (±CI)[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]StdDev[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]CV[/]").RightAligned());

        foreach (var percentile in percentileKeys)
        {
            var key = BenchmarkTable.FormatPercentileKey(percentile);
            table.AddColumn(new TableColumn($"[dim]P{key}[/]").RightAligned());
        }

        foreach (var row in rows)
        {
            var cvColor = row.CoefficientOfVariationPercent switch
            {
                <= 2.0 => "green",
                <= 5.0 => "yellow",
                _ => "red",
            };

            var cells = new List<string>
            {
                $"[dim]{Esc(row.Name)}[/]",
            };

            if (showRuntime)
                cells.Add(Esc(row.RuntimeMoniker));

            cells.AddRange([
                $"±{BenchmarkFormatter.FormatNs(row.MarginOfError)} [dim]({row.MarginPercent:F2}%)[/]",
                BenchmarkFormatter.FormatNs(row.StandardDeviation),
                $"[{cvColor}]{row.CoefficientOfVariationPercent:F2}%[/]",
            ]);

            foreach (var percentile in percentileKeys)
            {
                var value = row.GetPercentile(percentile);
                cells.Add(value.HasValue ? BenchmarkFormatter.FormatNs(value.Value) : "-");
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderLaunchStats(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Errored && r.LaunchStatistics is not null).ToList();

        if (rows.Count == 0)
            return;

        var rule = new Rule("[dim]Launch Aggregation[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey42)
            .AddColumn(new TableColumn("[dim]Benchmark[/]").NoWrap())
            .AddColumn(new TableColumn("[dim]Launches[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]Mean[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]StdDev[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]Median[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]CI (95%)[/]").RightAligned());

        foreach (var row in rows)
        {
            var ls = row.LaunchStatistics!;

            var ciText = ls.LaunchConfidenceIntervalLower.HasValue && ls.LaunchConfidenceIntervalUpper.HasValue
                ? $"[[{ls.LaunchConfidenceIntervalLower!.Value:F1}–{ls.LaunchConfidenceIntervalUpper!.Value:F1}]]"
                : "[dim]-[/]";

            table.AddRow(
                $"[dim]{Esc(row.Name)}[/]",
                $"{ls.LaunchCount}",
                BenchmarkFormatter.FormatNs(ls.LaunchMean),
                BenchmarkFormatter.FormatNs(ls.LaunchStandardDeviation),
                BenchmarkFormatter.FormatNs(ls.LaunchMedian),
                ciText
            );
        }

        AnsiConsole.Write(table);
    }

    private static void RenderAutoTune(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Errored && r.AutoTune is not null).ToList();

        if (rows.Count == 0)
            return;

        AnsiConsole.WriteLine();

        foreach (var row in rows)
        {
            AnsiConsole.MarkupLine($"[grey]{Esc(row.Name)}: {Esc(BenchmarkTable.FormatAutoTuneSummary(row.AutoTune!))}[/]");
        }
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

        var hasMultipleRuntimes = benchTable.Rows
            .Where(r => !r.Errored)
            .Select(r => r.RuntimeMoniker)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1;

        if (hasMultipleRuntimes)
            AnsiConsole.MarkupLine("[grey]Omnibus:[/] [dim]runtime-scoped in multi-runtime runs; combined summary omitted.[/]");
        else if (benchTable.Omnibus is { } omnibus)
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
            AnsiConsole.MarkupLine("[grey]Omnibus:[/] [dim]not run (fewer than 3 comparable groups)[/]");

        var testName = hasMultipleRuntimes
            ? benchTable.SignificanceTestName
            : benchTable.Omnibus?.TestName ?? benchTable.SignificanceTestName;

        AnsiConsole.MarkupLine(
            $"[grey]Significance:[/] [dim]{Esc(testName)} (p < {benchTable.SignificanceLevel:0.###})[/]");

        AnsiConsole.MarkupLine($"[grey]Outliers:[/] [dim]{Esc(benchTable.OutlierDetector)}[/]");

        AnsiConsole.MarkupLine(
            $"[grey]Effect metric:[/] [dim]{Esc(GetEffectMetricSummary(benchTable.Rows))}[/]");

        var profileLabel = benchTable.Profile switch
        {
            MeasurementProfile.Independent => "independent (per-iteration GC, between-benchmark GC, no alloc tracking)",
            _ => "realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)",
        };
        AnsiConsole.MarkupLine($"[grey]Profile:[/] [dim]{profileLabel}[/]");

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
            return "[yellow]sml[/]";

        if (string.Equals(magnitude, "med", StringComparison.OrdinalIgnoreCase)
            || string.Equals(magnitude, "medium", StringComparison.OrdinalIgnoreCase))
            return "[orange1]med[/]";

        if (string.Equals(magnitude, "large", StringComparison.OrdinalIgnoreCase))
        {
            return value.Direction switch
            {
                EffectDirection.CandidateHigher => "[bold red]lrg[/]",
                EffectDirection.CandidateLower => "[bold green]lrg[/]",
                _ => "[bold]lrg[/]",
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
