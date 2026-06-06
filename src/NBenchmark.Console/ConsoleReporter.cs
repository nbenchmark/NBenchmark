using global::NBenchmark;
using global::NBenchmark.Reporters;
using Spectre.Console;

namespace NBenchmark.Console;

public sealed class ConsoleReporter : IReporter
{
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

        var multiBenchmark = results.Count > 1;
        var successful = results.Where(r => !r.Errored).ToList();

        if (successful.Count == 0)
        {
            foreach (var result in results)
                AnsiConsole.MarkupLine($"[red]Error: {EscapeMarkup(result.Name)}: {EscapeMarkup(result.ErrorMessage)}[/]");
            return Task.CompletedTask;
        }

        var headerSource = successful[0];
        AnsiConsole.MarkupLine($"[grey]Run at {headerSource.RunAt:yyyy-MM-dd HH:mm:ss} UTC — "
                             + $"{headerSource.WarmupIterations} warmup / "
                             + $"{headerSource.MeasuredIterations} measured[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
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
            table.AddColumn("Description");

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                    ?? successful.MinBy(r => r.Median)!;
        var totalDuration = results.Aggregate(TimeSpan.Zero, (a, r) => a + r.TotalDuration);

        foreach (var result in results.OrderBy(r => r.Median))
        {
            if (result.Errored)
            {
                var errorCols = new List<string>
                {
                    $"[red][Error] {EscapeMarkup(result.Name)}[/]",
                    "[red]—[/]", "[red]—[/]", "[red]—[/]",
                    "[red]—[/]", "[red]—[/]", "[red]—[/]", "[red]—[/]", "[red]—[/]"
                };
                if (hasDescriptions)
                    errorCols.Add("[red]—[/]");
                table.AddRow(errorCols.ToArray());
                AnsiConsole.MarkupLine($"[red]  Error: {EscapeMarkup(result.ErrorMessage)}[/]");
                continue;
            }

            var ratio = baseline.Median == 0 ? double.NaN : result.Median / baseline.Median;
            var ratioCol = double.IsNaN(ratio)
                ? "[grey]N/A[/]"
                : result.IsBaseline
                    ? "[grey]1.00x[/]"
                    : ratio <= 1.05 ? $"[green]{ratio:F2}x[/]"
                    : ratio <= 1.5 ? $"[yellow]{ratio:F2}x[/]"
                    : $"[red]{ratio:F2}x[/]";

            var significanceCol = "";
            if (multiBenchmark && !result.IsBaseline && result.IsSignificant.HasValue)
            {
                significanceCol = result.IsSignificant.Value
                    ? " [green]✓[/]"
                    : " [grey]~[/]";
            }

            var safeName = EscapeMarkup(result.Name);
            var nameCol = result.IsBaseline
                ? $"[bold]{safeName}[/] [grey](baseline)[/]"
                : ratio <= 1.05 ? $"[green]{safeName}[/]"
                : ratio <= 1.5 ? $"[yellow]{safeName}[/]"
                : $"[red]{safeName}[/]";

            var rowCols = new List<string>
            {
                $"{nameCol}{significanceCol}",
                BenchmarkFormatter.FormatNs(result.Median),
                BenchmarkFormatter.FormatNs(result.Mean),
                $"[grey]±{BenchmarkFormatter.FormatNs(result.MarginOfError)}[/]",
                BenchmarkFormatter.FormatNs(result.StandardDeviation),
                BenchmarkFormatter.FormatNs(result.P95),
                BenchmarkFormatter.FormatNs(result.P99),
                ratioCol,
                result.MeanAllocatedBytes.HasValue
                    ? BenchmarkFormatter.FormatBytes(result.MeanAllocatedBytes.Value)
                    : "[grey]-[/]"
            };
            if (hasDescriptions)
                rowCols.Add(string.IsNullOrEmpty(result.Description) ? "" : EscapeMarkup(result.Description));
            table.AddRow(rowCols.ToArray());
        }

        AnsiConsole.Write(table);

        if (successful.Count > 1)
        {
            AnsiConsole.WriteLine();
            var chart = new BarChart()
                .Width(60)
                .Label("[bold]Median (ns)[/]")
                .CenterLabel();

            foreach (var result in successful.OrderBy(r => r.Median))
                chart.AddItem(result.Name, Math.Round(result.Median, 1), Color.SteelBlue1);

            AnsiConsole.Write(chart);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[grey]Ran {results.Count} benchmark(s) in {totalDuration.TotalSeconds:F1}s — "
            + $"Significance: Mann-Whitney U (p < 0.05) — "
            + $"Outliers: {FormatOutlierMode(results.FirstOrDefault()?.OutlierMode ?? OutlierMode.RemoveTop5Percent)}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Error = ±{successful[0].ConfidenceLevel * 100:0.#}% confidence interval half-width on the mean.[/]");

        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    private static string FormatOutlierMode(OutlierMode mode) => mode switch
    {
        OutlierMode.None => "none",
        OutlierMode.RemoveTop5Percent => "top 5%",
        OutlierMode.RemoveTop5PercentAndBottom5Percent => "top & bottom 5%",
        OutlierMode.IqrFence => "IQR fence (1.5×)",
        _ => "auto",
    };

    private static string EscapeMarkup(string? text) =>
        text?.Replace("[", "[[").Replace("]", "]]") ?? "";
}
