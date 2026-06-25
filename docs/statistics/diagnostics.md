---
title: Diagnostics
description: Runtime counters for GC pressure, heap state, exceptions, and CPU usage.
order: 6
---

# Diagnostics

Allocation bytes tell you how much memory a benchmark allocated, but they do not explain *why* it is slow or *how* the garbage collector responded. Diagnostics add runtime counters alongside the timing and allocation data so you can distinguish steady-state code from allocation-heavy code, CPU-bound work from IO-bound work, and normal control flow from exception-driven control flow.

## What is collected

Four counters are available, each independently toggleable via `DiagnosticsOptions`:

### GC collection counts

Gen0, Gen1, and Gen2 collection counts during the measurement phase, reported as totals (not per-operation rates). Collected via `GC.CollectionCount(n)` bracketed around each sample.

**Why it matters:** Allocation bytes are a proxy for memory pressure. Collection counts show *actual* GC pressure. A benchmark that allocates heavily but stays in Gen0 has different characteristics from one that triggers Gen1 or Gen2 collections. The totals let you compare: a body that causes 0 Gen2 collections across the measurement phase is steady-state; one that causes many is allocation-heavy.

**Overhead:** negligible. `GC.CollectionCount` is a counter read, not a measurement operation.

**Default:** on (`GcCollectionCounts = true`). This is cheap enough to always collect.

### GC heap info

Heap committed bytes and fragmented bytes, reported as a delta across the measurement phase via `GC.GetGCMemoryInfo()`. A snapshot is taken before measurement begins and after it ends; the difference is reported.

**Why it matters:** Shows how the benchmark affected the managed heap. A growing committed footprint or rising fragmentation signals that the body is not releasing memory efficiently, even if per-iteration allocations look small.

**Overhead:** low. Two `GetGCMemoryInfo` calls per benchmark (one before measurement, one after).

**Default:** off. Enable with `GcHeapInfo = true` or `--diagnostics all`.

### Exception count

Total first-chance exceptions thrown during the measurement phase, divided by total measurement operations to give exceptions per operation. Collected via an `AppDomain.CurrentDomain.FirstChanceException` subscription scoped to the measurement loop.

**Why it matters:** Exception-driven control flow is a common hidden cost. `TryParse` failures, regex matching against non-matching input, and serialization fallbacks all throw first-chance exceptions that do not appear in any other metric. A high exceptions-per-op value explains latency that allocation and timing data cannot.

**Overhead:** moderate. The `FirstChanceException` event fires on every exception in the process during the subscription window, not just benchmark exceptions. NBenchmark subscribes only for the measurement phase and unsubscribes in a `finally` block so the handler cannot leak.

**Default:** off. Enable with `Exceptions = true` or `--diagnostics all`.

### CPU time

Process CPU time (via `Process.GetCurrentProcess().TotalProcessorTime`) bracketed around each sample, divided by total measurement operations to give CPU nanoseconds per operation. Also reports the CPU/wall-clock ratio (`CpuTime / MeasuredDuration`).

**Why it matters:** The CPU/wall-clock ratio distinguishes CPU-bound benchmarks (ratio near the core count) from IO-bound or wait-bound benchmarks (ratio well below the core count). A ratio of `1.0` on a single-core run means the body is purely CPU-bound; a ratio of `0.25` on a 4-core machine means 75% of the wall-clock time was spent waiting.

**Overhead:** low. `TotalProcessorTime` is a counter read, not a measurement operation.

**Default:** off. Enable with `CpuTime = true`, `--diagnostics gcandcpu`, or `--diagnostics all`.

> [!NOTE]
> The CPU/wall-clock ratio is process-wide and can exceed `1.0` on multi-core machines. A multi-threaded benchmark on a 4-core machine can show up to `4.0` (400%). In console output, CPU% is colour-coded from the raw ratio: green at 85%+, yellow at 50-85%, red below 50%.

## How collection works

Diagnostics are collected during Phase C (measurement) only. Calibration and warmup phases do not collect diagnostics, mirroring the allocation-tracking pattern.

For each sample, `DiagnosticMeter.Capture()` reads the current counter values before the body loop runs, and `DiagnosticMeter.Delta()` computes the difference after. The per-sample deltas are stored in a `DiagnosticDelta` array on `AdaptiveResult`.

Exception counting uses a separate mechanism: `ExceptionCounter.Subscribe()` attaches a `FirstChanceException` handler before the measurement loop, and `ExceptionCounter.Unsubscribe()` detaches it in a `finally` block. The handler increments a thread-safe counter via `Interlocked.Increment`. The total count is read after the loop and divided by total measurement operations.

Heap info is a per-benchmark snapshot, not per-sample: `GC.GetGCMemoryInfo()` is called once before the measurement loop and once after. The delta (committed and fragmented bytes) is reported directly.

## The DiagnosticsResult record

All collected counters are available on `BenchmarkResult.Diagnostics` as a `DiagnosticsResult?`:

| Field | Type | Meaning |
|---|---|---|
| `Gen0Collections` | `long?` | Total Gen0 collections during measurement. |
| `Gen1Collections` | `long?` | Total Gen1 collections during measurement. |
| `Gen2Collections` | `long?` | Total Gen2 collections during measurement. |
| `HeapCommittedBytes` | `long?` | Heap committed bytes delta. |
| `HeapFragmentedBytes` | `long?` | Heap fragmented bytes delta. |
| `ExceptionCountPerOp` | `double?` | Exceptions per operation (total exceptions / measurement ops). |
| `CpuTimeNsPerOp` | `double?` | CPU time per operation in nanoseconds. |
| `CpuWallRatio` | `double?` | CPU time / wall-clock time (process-wide, can exceed 1.0 on multi-core). |
| `Mode` | `DiagnosticsMode` | Which counters were collected. |

All fields are `null` when the corresponding toggle was off, when diagnostics are disabled (`DiagnosticsOptions.None`), or when the run errored.

## Configuring diagnostics

### Programmatic (any mode)

```csharp
// Single mode
var result = Benchmark.Run(() => MyMethod(), options: new MeasurementOptions
{
    Diagnostics = DiagnosticsOptions.All,
});

// Suite mode
await new BenchmarkSuite("MySuite")
    .WithDiagnostics(DiagnosticsMode.All)
    .Add("MethodA", () => MethodA())
    .RunAsync();

// Harness mode
BenchmarkHarness.Create(args)
    .WithDiagnostics(DiagnosticsMode.GcAndCpu)
    .RunAsync();
```

### Custom combinations

`DiagnosticsOptions` is a record, so you can enable any combination of toggles:

```csharp
// GC counts + exceptions, no CPU time or heap info
var options = new MeasurementOptions
{
    Diagnostics = new DiagnosticsOptions
    {
        GcCollectionCounts = true,
        Exceptions = true,
    },
};
```

### CLI (Harness mode)

```bash
# Default - GC counts only
dotnet run -- --diagnostics gc

# GC counts + CPU time
dotnet run -- --diagnostics gcandcpu

# Everything
dotnet run -- --diagnostics all

# Disable all diagnostics
dotnet run -- --diagnostics none
```

The `--diagnostics` flag overrides any programmatic `Diagnostics` setting, mirroring how `--no-allocations` overrides `MeasureAllocations`.

## Reporting

### Diagnostics table

At `standard` and `advanced` detail levels, a separate **Diagnostics** table appears below the Precision & Tail Latency table. The table only renders when at least one benchmark has diagnostics data. Columns are dynamic - only columns with data appear:

| Column | Source | When it appears |
|---|---|---|
| Benchmark | Benchmark name | Always (when table renders) |
| Runtime | Runtime moniker | When multi-runtime results are present |
| Gen0, Gen1, Gen2 | Collection count totals | When `GcCollectionCounts` is on |
| Heap | Committed bytes (formatted) | When `GcHeapInfo` is on |
| CPU% | CPU/wall ratio as a percentage | When `CpuTime` is on |
| Exc/op | Exceptions per operation | When `Exceptions` is on |

### Advanced stats block

At `advanced` detail, the per-benchmark stats block (shown below each console row, or in the Markdown details section) includes a **Diagnostics** sub-block with the same fields in a vertical layout:

```
Diagnostics:
  Gen0: 12   Gen1: 0   Gen2: 0
  Heap: 1.2 MB (fragmented 80 KB)
  CPU: 98% (1.2 µs/op)
  Exc/op: 0.0033
```

### CSV columns

The CSV reporter adds diagnostics columns at all detail levels:

| Detail level | Columns added |
|---|---|
| Simple | `Gen0`, `Gen1`, `Gen2` |
| Standard | `Gen0`, `Gen1`, `Gen2` |
| Advanced | `Gen0`, `Gen1`, `Gen2`, `HeapCommitted`, `HeapFragmented`, `ExceptionPerOp`, `CpuTimeNsPerOp`, `CpuWallRatio`, `DiagnosticsMode` |

Null values render as empty fields, consistent with other optional columns.

### JSON

The JSON reporter serializes the full `BenchmarkResult` record, so `Diagnostics` appears automatically as a `diagnostics` object with all non-null fields. No configuration needed.

### Live progress

Both the default and Spectre.Console progress displays append a GC summary to the `OnBenchmarkCompleted` line when GC collection counts are available:

```
  ✓ MyBenchmark  42.3 ns · 12/0/0 GC  (1.2s)
```

The three numbers are Gen0/Gen1/Gen2 collection totals.

## Platform compatibility

All four counters use BCL APIs available on every .NET runtime NBenchmark targets (net8.0, net9.0, net10.0) and on all operating systems (Linux, macOS, Windows). No platform-specific code or elevated privileges are required.

Hardware performance counters (instructions retired, cache misses, branch mispredictions) are not included. Those require platform-specific APIs (`perf_event_open` on Linux, kernel drivers on Windows, unavailable on macOS) and are deferred to a potential future `NBenchmark.Diagnostics.HardwareCounters` package.

## See also

- [Configuration: Diagnostics](../reference/configuration.md#diagnostics) - the `DiagnosticsOptions` surface
- [CLI Reference: `--diagnostics`](../reference/cli.md#--diagnostics-mode) - the CLI flag
- [Allocation Measurement](./allocations.md) - how per-iteration heap allocation is sampled
- [Report Detail Levels](../output/report-detail-levels.md) - how the Diagnostics table fits into each detail tier