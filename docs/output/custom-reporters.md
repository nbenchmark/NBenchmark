---
title: Custom Reporters
description: Implement IReporter to create your own output format, and register it with ReporterRegistry for CLI use.
order: 6
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

Add it to your harness or suite with `.WithReporter(new MyReporter())`.

If you want your custom reporter to be usable from the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
using NBenchmark.Reporters;

// In a static constructor or [ModuleInitializer]:
ReporterRegistry.Register("my-reporter", "Custom output", _ => new MyReporter());
```

After registration, `--reporter my-reporter` works from the CLI.

## Auto-attached reporters

Reporters come in two flavours:

- **Explicit opt-in** reporters, registered via `ReporterRegistry.Register`. These only fire when the user passes `--reporter <name>` on the CLI or calls `.WithReporter(...)` programmatically. The built-in `json`, `markdown`, and `csv` reporters, and the optional `console` reporter, are all explicit opt-in.
- **Auto-attached** reporters, registered via `ReporterRegistry.RegisterAutoAttach`. These fire on **every** run, after the user's explicit reporters, with no opt-in required. They are designed for side-effect reporters that integrate with an external system - for example, a reporter that writes run results to a file inbox for a separate Studio process to ingest.

The two registration paths are mutually exclusive: the same name cannot be registered via both `Register` and `RegisterAutoAttach` (case-insensitive). Auto-attached reporters are listed separately on `ReporterRegistry.AutoAttached` and shown in the `--reporter` flag's help line so users can see what is auto-firing.

### Self-registering an auto-attached reporter

External packages self-register their auto-attached reporters via a `[ModuleInitializer]` that calls `ReporterRegistry.RegisterAutoAttach`, mirroring how `NBenchmark.Reporters.Console` self-registers the `console` reporter via `Register`. The `[ModuleInitializer]` runs when the package's assembly is loaded by the host process, which happens on the first call to `ReporterRegistry.Available`, `ReporterRegistry.AutoAttached`, or `ReporterRegistry.CreateAutoAttachedReporters`.

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

Reference the package from the user's benchmark project and the reporter fires on every `BenchmarkHarness.RunAsync` and `BenchmarkSuite.RunAsync` call - no per-run setup required.

### Dedup with explicit reporters

If the user also adds an auto-attached reporter as an explicit reporter (via `--reporter <name>` or `.WithReporter(...)` with an instance whose `Name` matches the auto-attached reporter's name), the auto-attached one is skipped for that run so the reporter does not fire twice. The dedup is by canonical name, case-insensitive.

### Resilience

A misbehaving auto-attached reporter cannot kill the run. Each auto-attached reporter's `ReportAsync` is wrapped in try/catch with `Trace.TraceWarning`; the exception is logged and the run continues to the next reporter. This mirrors `CompositeMeasurementObserver`'s per-dispatch isolation. The user's `BenchmarkResult` list is still returned from `RunAsync` and any subsequent explicit reporters still run.

### CI / opt-out convention

Because auto-attached reporters fire on every run by design, packages that ship one are expected to follow a standard opt-out convention so CI pipelines and explicit opt-out users are not polluted with side-effect writes:

- The reporter's `ReportAsync` should no-op when the `CI=true` environment variable is set (the standard convention set by GitHub Actions, GitLab CI, Azure Pipelines, etc.).
- The reporter should also accept a package-specific disable env var (e.g. `NBENCHMARK_MYTOOL_DISABLE=1`) as an explicit escape hatch for users who want the package referenced locally but do not want every benchmark run written to the sink.
- Both guards should run before any directory creation or file I/O so a CI runner with neither env var set pays only the cost of two `GetEnvironmentVariable` calls.

The contract is convention, not enforced by NBenchmark - the package owning the reporter is responsible for honouring it.

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
