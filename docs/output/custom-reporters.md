---
title: Custom Reporters
description: Implement IReporter to create your own output format, and register it with ReporterRegistry for CLI use.
order: 5
---

# Custom Reporters

## Writing a custom reporter

Implement `IReporter` from the `NBenchmark` package:

```csharp
public sealed class MyReporter : IReporter
{
    public string Name => "my-reporter";

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        foreach (var result in results.Where(r => !r.Errored))
        {
            Console.WriteLine($"{result.Name}: median={result.Median:F0}ns");
        }
    }
}
```

Add it to your host or suite with `.WithReporter(new MyReporter())`.

If you want your custom reporter to be usable from the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
using NBenchmark.Reporters;

// In a static constructor or [ModuleInitializer]:
ReporterRegistry.Register("my-reporter", "Custom output", _ => new MyReporter());
```

After registration, `--reporter my-reporter` works from the CLI.

## Using BenchmarkTable in a custom reporter

For reporters that produce comparison tables, use `BenchmarkTable.Build(results)` rather than working with `IReadOnlyList<BenchmarkResult>` directly. It centralises the logic you would otherwise duplicate:

- **Baseline selection** - picks the first result marked `[Baseline]`, or falls back to the fastest (lowest median) if none is marked.
- **Ratio computation** - `row.Ratio` is `result.Median / baseline.Median`, or `NaN` for errored results or single-benchmark runs.
- **Significance labels** - `row.SignificanceLabel` is `"✓"` (significant), `"✗"` (not significant), or `""` (not applicable).
- **Ordering** - rows are sorted by median ascending.
- **Run metadata** - `table.RunAtUtc`, `table.WarmupIterations`, `table.MeasuredIterations`, `table.ConfidenceLevel`, `table.OutlierDetector` (the detector's display name, e.g. `"IQR fence (1.5×)"` or `"MAD (3×)"`), `table.SignificanceTestName` (the pairwise test's name), and `table.TotalDuration` are available for building a header without picking fields from individual results.
- **Omnibus verdict** - `table.Omnibus` is non-`null` when an omnibus test ran (Kruskal-Wallis across three or more groups). It exposes `TestName`, `Statistic`, `DegreesOfFreedom`, `GroupCount`, `PValue`, and `Verdict` so you can render a single across-all-groups line.

```csharp
public async Task ReportAsync(
    IReadOnlyList<BenchmarkResult> results,
    CancellationToken cancellationToken = default)
{
    var table = BenchmarkTable.Build(results);

    Console.WriteLine(
        $"Run at {table.RunAtUtc} UTC - {table.WarmupIterations} warmup / {table.MeasuredIterations} measured");

    foreach (var row in table.Rows)
    {
        if (row.Errored)
        {
            Console.WriteLine($"{row.Name}: ERROR - {row.ErrorMessage}");
            continue;
        }

        var sig = row.SignificanceLabel is "" ? "" : $" {row.SignificanceLabel}";
        Console.WriteLine($"{row.Name}{sig}: {row.Median:F0} ns  ratio={row.Ratio:F2}x");
    }
}
```
