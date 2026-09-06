---
title: Custom Reporters
description: Implement IReporter to create your own output format, and register it with ReporterRegistry for CLI use.
order: 6
---

# Custom Reporters

## Writing a custom reporter

Implement the `IReporter` interface from the `NBenchmark` package to create a custom output format:

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

Attach the reporter to your harness or suite using `.WithReporter(new MyReporter())`.

If you want your custom reporter to be usable via the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
using NBenchmark.Reporters;

// In a static constructor or [ModuleInitializer]:
ReporterRegistry.Register("my-reporter", "Custom output", _ => new MyReporter());
```

After registration, the `--reporter my-reporter` flag works from the CLI.

## Auto-attached reporters

NBenchmark supports two types of reporters:

- **Explicit opt-in reporters**: Registered via `ReporterRegistry.Register`. These only run when you pass `--reporter <name>` on the CLI or call `.WithReporter(...)` programmatically. Built-in reporters (such as `json`, `markdown`, and `csv`) and the optional `console` reporter are explicit opt-in.
- **Auto-attached reporters**: Registered via `ReporterRegistry.RegisterAutoAttach`. These run on **every** run after the user's explicit reporters, with no opt-in required. These are intended for side-effect reporters that integrate with external systems, such as a reporter that writes results to a file inbox for a separate Studio process.

Registration paths are mutually exclusive: you cannot register the same name via both `Register` and `RegisterAutoAttach` (case-insensitive). Auto-attached reporters are listed separately in `ReporterRegistry.AutoAttached` and appear in the `--reporter` flag's help line.

### Self-registering an auto-attached reporter

External packages self-register auto-attached reporters using a `[ModuleInitializer]` that calls `ReporterRegistry.RegisterAutoAttach`. This mirrors how `NBenchmark.Reporters.Console` registers the `console` reporter. The `[ModuleInitializer]` runs when the host process loads the package's assembly, which happens on the first call to `ReporterRegistry.Available`, `ReporterRegistry.AutoAttached`, or `ReporterRegistry.CreateAutoAttachedReporters`.

```csharp
using System.Runtime.CompilerServices;
using NBenchmark.Reporters;

namespace MyPackage.Reporters;

internal static class MyReporterRegistration
{
    [ModuleInitializer]
    internal static void Register() =>
        ReporterRegistry.RegisterAutoAttach(
            "my-sink",
            "Writes run results to a file inbox for MyTool to ingest",
            (_, detail) => new MySinkReporter { Detail = detail });
}
```

Once you reference the package in the benchmark project, the reporter runs on every `BenchmarkHarness.RunAsync` and `BenchmarkSuite.RunAsync` call without per-run setup.

### Deduplication with explicit reporters

If you add an auto-attached reporter as an explicit reporter (via `--reporter <name>` or `.WithReporter(...)`), NBenchmark skips the auto-attached version for that run to prevent the reporter from firing twice. Deduplication is based on the canonical name (case-insensitive).

### Resilience

A misbehaving auto-attached reporter cannot crash the run. NBenchmark wraps each auto-attached reporter's `ReportAsync` call in a try/catch block and logs exceptions via `Trace.TraceWarning`. If an auto-attached reporter fails, the run continues to the next reporter. The `BenchmarkResult` list is still returned from `RunAsync`, and any subsequent explicit reporters still run.

### CI and opt-out conventions

Because auto-attached reporters fire on every run, packages that provide one should follow these standard opt-out conventions to avoid polluting CI pipelines:

- **CI Guard**: The reporter's `ReportAsync` should no-op when the `CI=true` environment variable is set (a standard convention used by GitHub Actions, GitLab CI, and Azure Pipelines).
- **Custom Guard**: The reporter should accept a package-specific disable environment variable (such as `NBENCHMARK_MYTOOL_DISABLE=1`) as an escape hatch for local users.
- **Performance**: Both guards should run before any directory creation or file I/O to minimize overhead.

This contract is a convention and is not enforced by NBenchmark; the package owner is responsible for honoring it.

## Using BenchmarkTable in a custom reporter

For reporters that produce comparison tables, use `BenchmarkTable.Build(results)` rather than working with `IReadOnlyList<BenchmarkResult>` directly. `BenchmarkTable` centralizes several common logic patterns:

- **Baseline selection**: Picks the first result marked `[Baseline]`, or falls back to the fastest (lowest median) if none is marked.
- **Ratio computation**: `row.Ratio` is `result.Median / baseline.Median`, or `NaN` for errored results or single-benchmark runs.
- **Composition**: a `BenchmarkRow` carries the measurement itself as `row.Result` and adds only what is relative to the baseline - `Ratio`, `RatioEstimate`, `RatioSuppressed`, `SignificanceLabel`, `IsBaseline`, and `BaseName`. Every other property is read through `row.Result`.
- **Significance labels**: `row.SignificanceLabel` is `"✓"` (significant), `"✗"` (not significant), or `""` (not applicable).
- **Ordering**: Rows are sorted by median ascending.
- **Run metadata**: Provides `table.RunAtUtc` (a `DateTimeOffset?`, `null` for an empty table), `table.WarmupIterations`, `table.MeasuredIterations`, `table.ConfidenceLevel`, `table.OutlierDetector` (the display name, such as `"IQR fence (1.5×)"`), `table.SignificanceTestName`, and `table.TotalDuration`.
- **Omnibus verdict**: `table.Omnibus` is non-`null` when an omnibus test runs (Kruskal-Wallis across three or more groups). It exposes `TestName`, `Statistic`, `DegreesOfFreedom`, `GroupCount`, `PValue`, and `Verdict`.

```csharp
public async Task ReportAsync(
    IReadOnlyList<BenchmarkResult> results,
    CancellationToken cancellationToken = default)
{
    var table = BenchmarkTable.Build(results);

    Console.WriteLine(
        $"Run at {table.RunAtUtc:yyyy-MM-dd HH:mm:ss} UTC - {table.WarmupIterations} warmup / {table.MeasuredIterations} measured");

    foreach (var row in table.Rows)
    {
        if (row.Result.Errored)
        {
            Console.WriteLine($"{row.Result.Name}: ERROR - {row.Result.ErrorMessage}");
            continue;
        }

        var sig = row.SignificanceLabel is "" ? "" : $" {row.SignificanceLabel}";
        Console.WriteLine($"{row.Result.Name}{sig}: {row.Result.Median:F0} ns  ratio={row.Ratio:F2}x");
    }
}
```
