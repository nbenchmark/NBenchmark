using System.Runtime.CompilerServices;
using Spectre.Console;

namespace NBenchmark.Reporters.Console;

public sealed class ConsoleReporter : IReporter
{
    public string Name => "console";

    public Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No results to display.[/]");
            return Task.CompletedTask;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Benchmark Results[/]");

        var benchTable = BenchmarkTable.Build(results);

        if (benchTable.Rows.All(r => r.Errored))
        {
            foreach (var row in benchTable.Rows)
            {
                AnsiConsole.MarkupLine($"[red]Error: {EscapeMarkup(row.Name)}: {EscapeMarkup(row.ErrorMessage)}[/]");
            }

            return Task.CompletedTask;
        }

        AnsiConsole.MarkupLine($"[grey]Run at {benchTable.RunAtUtc} UTC - "
                               + $"{benchTable.WarmupIterations} warmup / "
                               + $"{benchTable.MeasuredIterations} measured[/]");

        AnsiConsole.WriteLine();

        var consoleTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Benchmark")
            .AddColumn(new TableColumn("Median").Centered())
            .AddColumn(new TableColumn("Mean").Centered())
            .AddColumn(new TableColumn("Error").Centered())
            .AddColumn(new TableColumn("StdDev").Centered())
            .AddColumn(new TableColumn("P95").Centered())
            .AddColumn(new TableColumn("P99").Centered())
            .AddColumn(new TableColumn("Ratio").Centered())
            .AddColumn(new TableColumn("Alloc/op").Centered());

        var hasDescriptions = results.Any(r => !string.IsNullOrEmpty(r.Description));

        if (hasDescriptions)
            consoleTable.AddColumn("Description");

        foreach (var row in benchTable.Rows)
        {
            if (row.Errored)
            {
                var errorCols = new List<string>
                {
                    $"[red][[Error]] {EscapeMarkup(row.Name)}[/]",
                    "[red]-[/]", "[red]-[/]", "[red]-[/]",
                    "[red]-[/]", "[red]-[/]", "[red]-[/]", "[red]-[/]", "[red]-[/]",
                };

                if (hasDescriptions)
                    errorCols.Add("[red]-[/]");

                consoleTable.AddRow(errorCols.ToArray());
                AnsiConsole.MarkupLine($"[red]  Error: {EscapeMarkup(row.ErrorMessage)}[/]");
                continue;
            }

            var ratioCol = double.IsNaN(row.Ratio)
                ? "[grey]N/A[/]"
                : row.IsBaseline
                    ? "[grey]1.00x[/]"
                    : row.Ratio <= 1.05
                        ? $"[green]{row.Ratio:F2}x[/]"
                        : row.Ratio <= 1.5
                            ? $"[yellow]{row.Ratio:F2}x[/]"
                            : $"[red]{row.Ratio:F2}x[/]";

            var significanceCol = row.SignificanceLabel switch
            {
                "✓" => " [green]✓[/]",
                "~" => " [grey]~[/]",
                _ => "",
            };

            var safeName = EscapeMarkup(row.Name);

            var nameCol = row.IsBaseline
                ? $"[bold]{safeName}[/] [grey](baseline)[/]"
                : double.IsNaN(row.Ratio)
                    ? $"[grey]{safeName}[/]"
                    : row.Ratio <= 1.05
                        ? $"[green]{safeName}[/]"
                        : row.Ratio <= 1.5
                            ? $"[yellow]{safeName}[/]"
                            : $"[red]{safeName}[/]";

            var rowCols = new List<string>
            {
                $"{nameCol}{significanceCol}",
                BenchmarkFormatter.FormatNs(row.Median),
                BenchmarkFormatter.FormatNs(row.Mean),
                $"[grey]±{BenchmarkFormatter.FormatNs(row.MarginOfError)}[/]",
                BenchmarkFormatter.FormatNs(row.StandardDeviation),
                BenchmarkFormatter.FormatNs(row.P95),
                BenchmarkFormatter.FormatNs(row.P99),
                ratioCol,
                row.MeanAllocatedBytes.HasValue
                    ? BenchmarkFormatter.FormatBytes(row.MeanAllocatedBytes.Value)
                    : "[grey]-[/]",
            };

            if (hasDescriptions)
                rowCols.Add(string.IsNullOrEmpty(row.Description) ? "" : EscapeMarkup(row.Description));

            consoleTable.AddRow(rowCols.ToArray());
        }

        AnsiConsole.Write(consoleTable);

        var warnings = benchTable.Rows
            .Where(r => !r.Errored && r.Warnings.Count > 0)
            .ToList();

        if (warnings.Count > 0)
        {
            AnsiConsole.WriteLine();

            foreach (var row in warnings)
            {
                foreach (var warning in row.Warnings)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]! {EscapeMarkup(row.Name)}: {EscapeMarkup(warning)}[/]");
                }
            }
        }

        var successfulRows = benchTable.Rows.Where(r => !r.Errored).ToList();

        if (successfulRows.Count > 1)
        {
            AnsiConsole.WriteLine();

            var chart = new BarChart()
                .Width(60)
                .Label("[bold]Median (ns)[/]")
                .CenterLabel();

            foreach (var row in successfulRows)
            {
                chart.AddItem(row.Name, Math.Round(row.Median, 1), Color.SteelBlue1);
            }

            AnsiConsole.Write(chart);
        }

        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine(
            $"[grey]Ran {results.Count} benchmark(s) in {benchTable.TotalDuration.TotalSeconds:F1}s - "
            + $"Significance: Mann-Whitney U (p < {benchTable.SignificanceLevel:0.###}) - "
            + $"Outliers: {FormatOutlierMode(benchTable.OutlierMode)}[/]");

        AnsiConsole.MarkupLine(
            $"[grey]Error = ±{benchTable.ConfidenceLevel * 100:0.#}% confidence interval half-width on the mean.[/]");

        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    [ModuleInitializer]
    public static void Register() =>
        ReporterRegistry.Register(
            "console",
            "Console output (Spectre.Console table + bar chart)",
            _ => new ConsoleReporter());

    private static string FormatOutlierMode(OutlierMode mode)
    {
        return mode switch
        {
            OutlierMode.None => "none",
            OutlierMode.RemoveTop5Percent => "top 5%",
            OutlierMode.RemoveTopAndBottom5Percent => "top & bottom 5%",
            OutlierMode.IqrFence => "IQR fence (1.5×)",
            _ => "auto",
        };
    }

    private static string EscapeMarkup(string? text) => text?.Replace("[", "[[").Replace("]", "]]") ?? "";
}
