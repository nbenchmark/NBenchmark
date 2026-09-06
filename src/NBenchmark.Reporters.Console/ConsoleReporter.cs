using System.Runtime.CompilerServices;
using System.Text;
using NBenchmark.Stats;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NBenchmark.Reporters.Console;

public sealed class ConsoleReporter : IReporter
{
    private const int BarWidth = 12;
    private const int AxisMinWidth = 30;
    private const int AxisMaxWidth = 70;
    private const int StripIndent = 2;

    private const string GapStyle = "grey35";
    private const string WhiskerStyle = "grey62";
    private const string BoxStyle = "steelblue1";
    private const string MedianStyle = "bold yellow";
    private const string OutlierStyle = "indianred1";

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
                var className = table.CrossClass
                    ? null
                    : table.Rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.Result.ClassName))?.Result.ClassName;

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
            var className = table.CrossClass
                ? null
                : table.Rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.Result.ClassName))?.Result.ClassName;

            RenderComparisonTable(table, className, Detail);
            AnsiConsole.WriteLine();
            RenderTimingDetail(table);
            RenderDiagnostics(table);
            RenderLaunchStats(table);
            AnsiConsole.WriteLine();
            RenderInterpretation(table, table.Rows.Count);
            RenderAutoTune(table);

            if (Detail == ReportDetail.Advanced)
            {
                AnsiConsole.WriteLine();
                RenderAdvancedDetails(table);
                AnsiConsole.WriteLine();
                RenderDistribution(table);
            }

            AnsiConsole.WriteLine();
            RenderWarnings(table);
        }

        AnsiConsole.WriteLine();

        return Task.CompletedTask;
    }

    private static void RenderSimpleFooter(BenchmarkTable benchTable)
    {
        var count = benchTable.Rows.Count(r => !r.Result.Errored);

        // Runtime provenance is included even at Simple detail. The configuration a benchmark was
        // measured under moves the number by more than most of the effects people are looking for,
        // so it is not a detail-level nicety - a reader cannot interpret the table without it.
        AnsiConsole.MarkupLine(
            $"[dim]{count} benchmark(s) · {benchTable.TotalDuration.TotalSeconds:F1}s total · "
            + $"CI {benchTable.ConfidenceLevel * 100:0.#}% · runtime {Esc(RuntimeSummary(benchTable))}[/]");

        RenderMixedRuntimeProfileWarning(benchTable);
    }

    /// <summary>
    ///     A short description of the runtime configuration the rows were measured under. Reports
    ///     <c>mixed</c> rather than picking one arbitrarily when the rows disagree, which happens
    ///     whenever a class combines <c>[Isolation(Isolation.Off)]</c> benchmarks with isolated ones.
    /// </summary>
    private static string RuntimeSummary(BenchmarkTable benchTable)
    {
        if (benchTable.MixedRuntimeProfiles)
            return "mixed";

        if (benchTable.RuntimeProfileName != RuntimeProfile.Host.Name)
            return $"{benchTable.RuntimeProfileName} ({benchTable.RuntimeKnobs})";

        return string.IsNullOrEmpty(benchTable.RuntimeKnobs)
            ? "host (inherited - not applied by NBenchmark)"
            : $"host (inherited: {benchTable.RuntimeKnobs})";
    }

    private static void RenderMixedRuntimeProfileWarning(BenchmarkTable benchTable)
    {
        if (benchTable.MixedRuntimeProfiles)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] [dim]rows in this table were measured under different runtime "
                + "configurations, so their numbers are not comparable with each other. This usually "
                + "means in-process benchmarks were mixed with isolated ones.[/]");
        }

        if (benchTable.MixedIsolationStatuses)
        {
            AnsiConsole.MarkupLine(
                "[dim]Iso: whether the row was measured in an isolated worker process launched with "
                + "the requested runtime profile, or in this one.[/]");
        }

        var suppressed = benchTable.Rows.Count(r => r.RatioSuppressed);

        if (suppressed > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] [dim]{suppressed} row(s) show [bold]n/a[/] in place of a ratio. "
                + "They were not measured under the baseline's runtime configuration, and the "
                + "difference between two configurations is worth roughly 3.3x on bodies of "
                + "identical cost - so the ratio would have reported that, under the name of a "
                + "speedup. Compare rows measured the same way, or run the group without "
                + "[bold]--in-process[/] / [bold][[Isolation(Isolation.Off)]][/] so every row is isolated.[/]");
        }

        var inconclusive = benchTable.Rows
            .Where(r => !r.Result.Errored && !r.IsBaseline && r.RatioEstimate is { IncludesUnity: true })
            .ToList();

        if (inconclusive.Count > 0)
        {
            var replicates = inconclusive[0].RatioEstimate!.Replicates;

            AnsiConsole.MarkupLine(
                $"[dim]Ratios marked [bold]?[/] ({inconclusive.Count} row(s)) have a paired interval "
                + $"spanning 1.00x across {replicates} launches, so this run cannot tell those "
                + "benchmarks apart however far the number sits from 1.00. Raise "
                + "[bold]--launch-count[/] to narrow it, or read the ratio as \"no measured "
                + "difference\".[/]");

            // The one combination worth calling out rather than leaving to be noticed. A ✓ comes from
            // pooled within-run samples, whose count buys arbitrary power; the ratio interval comes
            // from between-launch spread, which is what a re-run would actually reproduce. When they
            // disagree the ✓ is the one to distrust.
            var significantButIrreproducible = inconclusive.Count(r => r.SignificanceLabel == "✓");

            if (significantButIrreproducible > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] [dim]{significantButIrreproducible} row(s) are marked "
                    + "significant ([bold]✓[/]) yet their ratio interval spans 1.00x. Significance is "
                    + "computed on samples pooled across launches, where a large count grants power "
                    + "regardless of reproducibility; the ratio interval is the run-to-run spread. "
                    + "Trust the interval.[/]");
            }
        }

        // Naming the reason matters as much as naming the fact. "You asked for in-process" and "the
        // measurement worker is not installed" produce identical numbers and identical labels, but
        // only one of them is a problem the user can fix.
        foreach (var reason in benchTable.InProcessReasons)
        {
            if (reason.ToRemedy() is not { } remedy)
                continue;

            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] [dim]{Esc(reason.ToLabel())} rows were not isolated: "
                + $"{Esc(remedy)}.[/]");
        }
    }

    private static void RenderHeader(BenchmarkTable benchTable)
    {
        var rule = new Rule($"[bold steelblue1]BENCHMARK RESULTS[/]  [grey]{benchTable.RunAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""} UTC[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("steelblue1"));

        AnsiConsole.Write(rule);
    }

    private static void RenderComparisonTable(BenchmarkTable benchTable, string? sectionClassName, ReportDetail detail)
    {
        var successfulRows = benchTable.Rows.Where(r => !r.Result.Errored).ToList();
        var maxMedian = successfulRows.Count > 0 ? successfulRows.Max(r => r.Result.Median) : 1;
        var showCategories = detail == ReportDetail.Advanced && benchTable.Rows.Any(r => r.Result.Categories.Count > 0);
        var showRuntime = benchTable.Rows.Any(r => r.Result.RuntimeMoniker.Length > 0);

        // Only when the rows disagree. On a uniform table the column would be a constant, and the
        // footer already names the configuration; when they disagree it is the difference between
        // reading the table and misreading it.
        var showIsolation = benchTable.MixedIsolationStatuses;
        var isSimple = detail == ReportDetail.Simple;
        var showClass = benchTable.CrossClass && benchTable.Rows.Any(r => r.Result.ClassName.Length > 0);

        // A ratio is present whenever a row was ranked against a reference - either a competing
        // benchmark in its parameter group or, for a single method swept across parameter values,
        // the fastest point in the table. Parametric tables use compact headers to save width.
        var hasComparisons = benchTable.Rows.Any(r => !r.Result.Errored && !double.IsNaN(r.Ratio));
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

        // Short labels, and a short header: at 80 columns a phrase here squeezes the numbers it
        // exists to qualify. Which rows were isolated goes in the column; why they were not goes in
        // the footer, which has a full line to say it in.
        // "Iso: yes/no" rather than a phrase. An 80-column table is already truncating its own
        // headers, and a wider column here buys its width from the measurements. Which rows were
        // isolated belongs in the table; why the others were not is a sentence, and goes in the
        // footer where there is a line to spend on it.
        if (showIsolation)
            table.AddColumn(new TableColumn("[bold]Iso[/]").Centered().NoWrap());

        foreach (var paramName in benchTable.ParameterNames)
        {
            table.AddColumn(new TableColumn($"[bold]{Esc(paramName)}[/]").RightAligned().NoWrap());
        }

        table
            .AddColumn(new TableColumn("[bold]Median[/]").RightAligned().NoWrap());

        if (!isSimple)
            table.AddColumn(new TableColumn("[bold]Mean[/]").RightAligned().NoWrap());

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

        var hasDescriptions = !isSimple && benchTable.Rows.Any(r => !string.IsNullOrEmpty(r.Result.Description));

        if (hasDescriptions)
            table.AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var row in benchTable.Rows)
        {
            var rawName = benchTable.ParameterNames.Count > 0 ? row.BaseName : row.Result.Name;

            var displayName = !string.IsNullOrEmpty(sectionClassName) && rawName.StartsWith(sectionClassName + ".", StringComparison.Ordinal)
                ? rawName[(sectionClassName.Length + 1)..]
                : rawName;

            if (row.Result.Errored)
            {
                var errorCols = new List<string> { $"[red]✗ {Esc(displayName)}[/]" };

                if (showClass)
                    errorCols.Add(Esc(row.Result.ClassName));

                if (showRuntime)
                    errorCols.Add(Esc(row.Result.RuntimeMoniker));

                if (showIsolation)
                    errorCols.Add("[dim]-[/]");

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

            var bar = RenderBar(row.Result.Median, maxMedian, ratioColor);

            string barCell;

            // The bar is decorative and costs 12 columns. In a mixed table those columns are needed
            // by the Iso column, which is not decorative: without it the reader cannot tell which
            // rows the n/a applies to. An 80-column terminal cannot have both.
            var showBar = !showIsolation;

            if (!hasComparisons)
                barCell = bar;
            else if (row.IsBaseline)
                barCell = showBar ? $"{bar} [dim]{ratioText}[/]" : $"[dim]{ratioText}[/]";
            else
                barCell = showBar ? $"{bar} [{ratioColor}]{ratioText}[/]" : $"[{ratioColor}]{ratioText}[/]";

            var allocText = row.Result.MeanAllocatedBytes.HasValue
                ? BenchmarkFormatter.FormatBytes(row.Result.MeanAllocatedBytes.Value)
                : "[dim]-[/]";

            var rowCols = new List<string> { nameText };

            if (showClass)
                rowCols.Add(Esc(row.Result.ClassName));

            if (showRuntime)
                rowCols.Add(Esc(row.Result.RuntimeMoniker));

            if (showIsolation)
            {
                rowCols.Add(row.Result.IsolationStatus.IsIsolated()
                    ? "[dim]yes[/]"
                    : "[yellow]no[/]");
            }

            rowCols.AddRange(ParameterCells(row, benchTable.ParameterNames));

            rowCols.AddRange([
                $"[bold]{BenchmarkFormatter.FormatNs(row.Result.Median)}[/]",
            ]);

            if (!isSimple)
                rowCols.Add(BenchmarkFormatter.FormatNs(row.Result.Mean));

            rowCols.AddRange([
                BenchmarkFormatter.FormatOpsPerSecond(row.Result.OperationsPerSecond),
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
                    rowCols.Add(RenderMagnitude(row.Result.Effect));
            }

            rowCols.Add(allocText);

            if (showCategories)
                rowCols.Add(row.Result.Categories.Count > 0 ? Esc(string.Join(", ", row.Result.Categories)) : "[dim]-[/]");

            if (hasDescriptions)
                rowCols.Add(string.IsNullOrEmpty(row.Result.Description) ? "" : Esc(row.Result.Description));

            table.AddRow(rowCols.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static IEnumerable<string> ParameterCells(BenchmarkRow row, IReadOnlyList<string> parameterNames)
    {
        foreach (var parameterName in parameterNames)
        {
            var parameter = row.Result.ParameterSet.FirstOrDefault(p => p.Name == parameterName);
            yield return parameter is null ? "[dim]-[/]" : Esc(BenchmarkParameter.FormatValue(parameter.Value));
        }
    }

    private static void RenderTimingDetail(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Result.Errored).ToList();
        var showRuntime = rows.Any(r => r.Result.RuntimeMoniker.Length > 0);

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
            .SelectMany(r => r.Result.Percentiles)
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
            var cvColor = row.Result.CoefficientOfVariationPercent switch
            {
                <= 2.0 => "green",
                <= 5.0 => "yellow",
                _ => "red",
            };

            var cells = new List<string>
            {
                $"[dim]{Esc(row.Result.Name)}[/]",
            };

            if (showRuntime)
                cells.Add(Esc(row.Result.RuntimeMoniker));

            cells.AddRange([
                $"±{BenchmarkFormatter.FormatNs(row.Result.MarginOfError)} [dim]({row.Result.MarginPercent:F2}%)[/]",
                BenchmarkFormatter.FormatNs(row.Result.StandardDeviation),
                $"[{cvColor}]{row.Result.CoefficientOfVariationPercent:F2}%[/]",
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
        var rows = benchTable.Rows.Where(r => !r.Result.Errored && r.Result.LaunchStatistics is not null).ToList();

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
            var ls = row.Result.LaunchStatistics!;

            var ciText = ls.LaunchConfidenceIntervalLower.HasValue && ls.LaunchConfidenceIntervalUpper.HasValue
                ? $"[[{ls.LaunchConfidenceIntervalLower!.Value:F1}–{ls.LaunchConfidenceIntervalUpper!.Value:F1}]]"
                : "[dim]-[/]";

            table.AddRow(
                $"[dim]{Esc(row.Result.Name)}[/]",
                $"{ls.LaunchCount}",
                BenchmarkFormatter.FormatNs(ls.LaunchMean),
                BenchmarkFormatter.FormatNs(ls.LaunchStandardDeviation),
                BenchmarkFormatter.FormatNs(ls.LaunchMedian),
                ciText
            );
        }

        AnsiConsole.Write(table);
    }

    private static void RenderDiagnostics(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Result.Errored && r.Result.Diagnostics is not null).ToList();

        if (rows.Count == 0)
            return;

        var rule = new Rule("[dim]Diagnostics[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey42)
            .AddColumn(new TableColumn("[dim]Benchmark[/]").NoWrap());

        var showRuntime = benchTable.Rows.Any(r => r.Result.RuntimeMoniker.Length > 0);

        if (showRuntime)
            table.AddColumn(new TableColumn("[dim]Runtime[/]").RightAligned().NoWrap());

        var hasGen0 = rows.Any(r => r.Result.Diagnostics!.Gen0Collections.HasValue);
        var hasHeap = rows.Any(r => r.Result.Diagnostics!.HeapCommittedBytes.HasValue);
        var hasCpu = rows.Any(r => r.Result.Diagnostics!.CpuWallRatio.HasValue);
        var hasExc = rows.Any(r => r.Result.Diagnostics!.ExceptionCountPerOp.HasValue);

        if (hasGen0)
        {
            table.AddColumn(new TableColumn("[dim]Gen0[/]").RightAligned());
            table.AddColumn(new TableColumn("[dim]Gen1[/]").RightAligned());
            table.AddColumn(new TableColumn("[dim]Gen2[/]").RightAligned());
        }

        if (hasHeap)
            table.AddColumn(new TableColumn("[dim]Heap[/]").RightAligned());

        if (hasCpu)
            table.AddColumn(new TableColumn("[dim]CPU%[/]").RightAligned());

        if (hasExc)
            table.AddColumn(new TableColumn("[dim]Exc/op[/]").RightAligned());

        foreach (var row in rows)
        {
            var cells = new List<IRenderable> { new Markup(Esc(row.Result.Name)) };

            if (showRuntime)
                cells.Add(new Markup(Esc(row.Result.RuntimeMoniker)));

            var diag = row.Result.Diagnostics!;

            if (hasGen0)
            {
                cells.Add(new Markup(diag.Gen0Collections?.ToString() ?? string.Empty));
                cells.Add(new Markup(diag.Gen1Collections?.ToString() ?? string.Empty));
                cells.Add(new Markup(diag.Gen2Collections?.ToString() ?? string.Empty));
            }

            if (hasHeap)
            {
                cells.Add(new Markup(diag.HeapCommittedBytes.HasValue
                    ? BenchmarkFormatter.FormatBytes(diag.HeapCommittedBytes.Value)
                    : string.Empty));
            }

            if (hasCpu)
            {
                if (diag.CpuWallRatio.HasValue)
                {
                    var cpuRatio = diag.CpuWallRatio.Value;

                    var cpuColor = cpuRatio switch
                    {
                        >= 0.85 => "green",
                        >= 0.50 => "yellow",
                        _ => "red",
                    };

                    cells.Add(new Markup($"[{cpuColor}]{cpuRatio * 100:F0}%[/]"));
                }
                else
                    cells.Add(new Markup(string.Empty));
            }

            if (hasExc)
            {
                cells.Add(new Markup(diag.ExceptionCountPerOp.HasValue
                    ? $"{diag.ExceptionCountPerOp.Value:F4}"
                    : string.Empty));
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderAutoTune(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Result.Errored && r.Result.AutoTune is not null).ToList();

        if (rows.Count == 0)
            return;

        AnsiConsole.WriteLine();

        foreach (var row in rows)
        {
            AnsiConsole.MarkupLine($"[grey]{Esc(row.Result.Name)}: {Esc(BenchmarkTable.FormatAutoTuneSummary(row.Result.AutoTune!))}[/]");
        }
    }

    private static void RenderAdvancedDetails(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows.Where(r => !r.Result.Errored).ToList();

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
                .Header($"[bold]{Esc(row.Result.Name)}[/]")
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
            .Where(r => !r.Result.Errored && r.Result.Warnings.Count > 0)
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
            foreach (var warning in row.Result.Warnings)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ {Esc(row.Result.Name)}:[/] [dim]{Esc(warning)}[/]");
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
            .Where(r => !r.Result.Errored)
            .Select(r => r.Result.RuntimeMoniker)
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

        AnsiConsole.MarkupLine($"[grey]Runtime:[/] [dim]{Esc(RuntimeSummary(benchTable))}[/]");

        RenderMixedRuntimeProfileWarning(benchTable);

        AnsiConsole.MarkupLine(
            $"[dim]{count} benchmark(s) · {benchTable.TotalDuration.TotalSeconds:F1}s total · CI {benchTable.ConfidenceLevel * 100:0.#}%[/]");
    }

    private static string GetEffectMetricSummary(IReadOnlyList<BenchmarkRow> rows)
    {
        var metrics = rows
            .Where(r => !r.Result.Errored)
            .Select(r => r.Result.Effect?.Metric)
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

    private static readonly char[] SparkBlocks =
        ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    private static void RenderDistribution(BenchmarkTable benchTable)
    {
        var rows = benchTable.Rows
            .Where(r => !r.Result.Errored && (r.Result.RawSamples.Count > 0 || r.Result.Max > r.Result.Min))
            .ToList();

        if (rows.Count == 0)
            return;

        var rule = new Rule("[dim]Distribution[/]")
            .LeftJustified()
            .RuleStyle(Style.Parse("grey"));

        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var axisWidth = ResolveAxisWidth();

        foreach (var row in rows)
        {
            var content = new StringBuilder();

            // The sparkline shows distribution shape (multimodality, skew); the box-whisker
            // strip below shows the quartiles, the kept range, and every trimmed sample as a
            // red dot at its true position. The strip is drawn purely from Q1/Q3/median and
            // the kept/discarded split, so it is correct for any outlier detector (IQR, MAD,
            // percentile, none, custom) - only which points are dots changes.
            if (row.Result.RawSamples.Count > 0)
            {
                // Give the sparkline the same width as the axis and indent it so its bins sit
                // directly above the axis cells - the two rows then read as a single chart.
                var (lo, _) = SampleRange(row);
                var indent = new string(' ', StripIndent + BenchmarkFormatter.FormatNs(lo).Length + 1);
                content.AppendLine(indent + RenderSparkline(row.Result.RawSamples, row.Result.TrimmedOrdinals, axisWidth));
            }

            content.AppendLine(RenderBoxWhisker(row, axisWidth));
            content.AppendLine(string.Empty);

            foreach (var line in RenderDistributionSummary(row, benchTable.OutlierDetector))
                content.AppendLine(line);

            var panel = new Panel(content.ToString().TrimEnd())
                .Header($"[bold]{Esc(row.Result.Name)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey42)
                .Padding(2, 1)
                .Expand();

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }
    }

    private static string RenderSparkline(IReadOnlyList<double> samples, IReadOnlyList<int> trimmedOrdinals, int bins)
    {
        if (samples.Count == 0)
            return string.Empty;

        var min = samples[0];
        var max = samples[0];

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i] < min)
                min = samples[i];

            if (samples[i] > max)
                max = samples[i];
        }

        var range = max - min;

        if (range <= 0)
        {
            var block = SparkBlocks[^1];
            return $"[steelblue1]{new string(block, bins)}[/]";
        }

        var counts = new int[bins];
        var trimmedSet = new HashSet<int>(trimmedOrdinals);
        var hasTrimmed = trimmedSet.Count > 0;

        var trimmedBins = hasTrimmed ? new bool[bins] : null;

        for (var i = 0; i < samples.Count; i++)
        {
            var bin = (int)((samples[i] - min) / range * bins);

            if (bin >= bins)
                bin = bins - 1;

            counts[bin]++;

            if (trimmedBins != null && trimmedSet.Contains(i))
                trimmedBins[bin] = true;
        }

        var maxCount = counts.Max();

        if (maxCount == 0)
            return string.Empty;

        var sb = new StringBuilder(bins * 16);

        for (var bin = 0; bin < bins; bin++)
        {
            var level = (int)((double)counts[bin] / maxCount * (SparkBlocks.Length - 1));
            level = Math.Clamp(level, 0, SparkBlocks.Length - 1);
            var block = SparkBlocks[level];

            if (hasTrimmed && counts[bin] > 0)
            {
                sb.Append(trimmedBins![bin] ? $"[red]{block}[/]" : $"[steelblue1]{block}[/]");
            }
            else
            {
                sb.Append($"[steelblue1]{block}[/]");
            }
        }

        return sb.ToString();
    }

    private static int ResolveAxisWidth()
    {
        // Reserve room for the two numeric axis labels, the spaces around the axis, and the
        // rounded panel border/padding (2 cols each side); give the rest to the axis itself.
        var available = Math.Max(0, AnsiConsole.Profile.Width - 32);
        return Math.Clamp(available, AxisMinWidth, AxisMaxWidth);
    }

    /// <summary>
    ///     Renders a to-scale box-and-whisker strip on a single min-&gt;max axis: the box spans
    ///     Q1-Q3 with the median marked, the whisker spans the kept (inlier) range, and every
    ///     trimmed sample is a red dot at its true position. Independent of the outlier
    ///     detector - the quartiles are always computed and the kept/discarded split is always
    ///     populated - so it reads the same way whether the fence was IQR, MAD, or otherwise.
    /// </summary>
    private static string RenderBoxWhisker(BenchmarkRow row, int width)
    {
        var (lo, hi) = SampleRange(row);

        if (hi <= lo || width < 4)
            return $"  [dim]▉ all samples ≈ {BenchmarkFormatter.FormatNs(row.Result.Median)}[/]";

        var cells = new char[width];
        var styles = new string[width];

        for (var i = 0; i < width; i++)
        {
            cells[i] = '·';
            styles[i] = GapStyle;
        }

        int Col(double v) => Math.Clamp((int)Math.Round((v - lo) / (hi - lo) * (width - 1)), 0, width - 1);

        // Whisker: the span of the kept (inlier) samples.
        var (keptLo, keptHi) = KeptExtent(row, lo, hi);
        var wLo = Col(keptLo);
        var wHi = Col(keptHi);

        for (var i = wLo; i <= wHi; i++)
        {
            cells[i] = '─';
            styles[i] = WhiskerStyle;
        }

        // Box: Q1-Q3 (clamped into the axis in case the pre-trim quartiles sit at an edge).
        var bLo = Col(Math.Clamp(row.Result.Q1, lo, hi));
        var bHi = Col(Math.Clamp(row.Result.Q3, lo, hi));

        for (var i = bLo; i <= bHi; i++)
        {
            cells[i] = '█';
            styles[i] = BoxStyle;
        }

        // Whisker end caps, only where the box did not already claim the cell.
        if (cells[wLo] == '─')
            cells[wLo] = '├';

        if (cells[wHi] == '─')
            cells[wHi] = '┤';

        // Median line.
        var mCol = Col(Math.Clamp(row.Result.Median, lo, hi));
        cells[mCol] = '◉';
        styles[mCol] = MedianStyle;

        // Trimmed samples as dots at their true positions.
        foreach (var ordinal in row.Result.TrimmedOrdinals)
        {
            if (ordinal < 0 || ordinal >= row.Result.RawSamples.Count)
                continue;

            var oCol = Col(row.Result.RawSamples[ordinal]);
            cells[oCol] = '●';
            styles[oCol] = OutlierStyle;
        }

        return $"  [dim]{BenchmarkFormatter.FormatNs(lo)}[/] {RunLengthMarkup(cells, styles)} [dim]{BenchmarkFormatter.FormatNs(hi)}[/]";
    }

    private static string[] RenderDistributionSummary(BenchmarkRow row, string outlierDetector)
    {
        var sampleCount = row.Result.RawSamples.Count > 0 ? row.Result.RawSamples.Count : row.Result.N + row.Result.OutliersRemoved;

        var stats =
            $"  [dim]median {BenchmarkFormatter.FormatNs(row.Result.Median)} · "
            + $"IQR {BenchmarkFormatter.FormatNs(row.Result.Q1)}–{BenchmarkFormatter.FormatNs(row.Result.Q3)}[/]";

        var counts = $"  [dim]{sampleCount} samples[/]";

        if (row.Result.OutliersRemoved > 0)
            counts += $"[red] · {row.Result.OutliersRemoved} trimmed ({Esc(outlierDetector)})[/]";

        return [stats, counts];
    }

    private static (double Lo, double Hi) SampleRange(BenchmarkRow row)
    {
        if (row.Result.RawSamples.Count == 0)
            return (row.Result.Min, row.Result.Max);

        var lo = row.Result.RawSamples[0];
        var hi = row.Result.RawSamples[0];

        for (var i = 1; i < row.Result.RawSamples.Count; i++)
        {
            if (row.Result.RawSamples[i] < lo)
                lo = row.Result.RawSamples[i];

            if (row.Result.RawSamples[i] > hi)
                hi = row.Result.RawSamples[i];
        }

        return (lo, hi);
    }

    private static (double Lo, double Hi) KeptExtent(BenchmarkRow row, double fallbackLo, double fallbackHi)
    {
        if (row.Result.RawSamples.Count == 0 || row.Result.TrimmedOrdinals.Count == 0)
            return (fallbackLo, fallbackHi);

        var trimmed = new HashSet<int>(row.Result.TrimmedOrdinals);
        var lo = double.PositiveInfinity;
        var hi = double.NegativeInfinity;

        for (var i = 0; i < row.Result.RawSamples.Count; i++)
        {
            if (trimmed.Contains(i))
                continue;

            if (row.Result.RawSamples[i] < lo)
                lo = row.Result.RawSamples[i];

            if (row.Result.RawSamples[i] > hi)
                hi = row.Result.RawSamples[i];
        }

        return double.IsInfinity(lo) || double.IsInfinity(hi) ? (fallbackLo, fallbackHi) : (lo, hi);
    }

    private static string RunLengthMarkup(char[] cells, string[] styles)
    {
        var sb = new StringBuilder(cells.Length * 4);
        var i = 0;

        while (i < cells.Length)
        {
            var style = styles[i];
            var start = i;

            while (i < cells.Length && styles[i] == style)
                i++;

            sb.Append('[').Append(style).Append(']').Append(cells, start, i - start).Append("[/]");
        }

        return sb.ToString();
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
        // "n/a" rather than "-": the dash means there was nothing to compare, and this row had
        // something to compare and was refused. A reader who cannot tell the two apart will assume
        // the tool simply did not compute it.
        if (row.RatioSuppressed)
            return ("n/a", "dim");

        if (double.IsNaN(row.Ratio))
            return ("-", "dim");

        if (row.IsBaseline)
            return ("baseline", "dim");

        // A ratio whose paired interval spans 1.00x is not a measured difference, whatever the point
        // estimate says. It is marked and dimmed rather than hidden: the number is still the best
        // estimate available, and colouring it red for "1.6x slower" when the run cannot distinguish
        // it from equal is how a reader is led to act on noise. The footer explains the mark.
        if (row.RatioEstimate is { IncludesUnity: true })
            return ($"{row.Ratio:F2}x?", "dim");

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
    internal static void Register() =>
        ReporterRegistry.Register(
            "console",
            "Console output (Spectre.Console table + bar chart)",
            (_, detail) => new ConsoleReporter(detail));

    private static string Esc(string? text) => Markup.Escape(text ?? "");
}
