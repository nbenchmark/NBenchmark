---
title: "Multi-runtime comparison"
description: Run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare results side-by-side.
order: 5
---

# Multi-runtime comparison

NBenchmark can run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare the results side-by-side. This is available in Suite mode (`WithRuntimes`), Harness mode (`--runtimes` CLI flag), and Harness mode via the `[Runtimes]` attribute.

## Project setup

The project must target all the runtimes you want to compare in its `.csproj` file:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

## Suite mode: `WithRuntimes`

Pass `RuntimeMoniker` values to `WithRuntimes`:

```csharp
var results = await new BenchmarkSuite("string-concat")
    .Add("concat", () => "a" + "b" + "c")
    .Add("interpolate", () => $"a {"b"} {"c"}")
    .WithBaseline("concat")
    .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)
    .WithWarmup(3)
    .WithIterations(50)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## Harness mode: `--runtimes` CLI flag

Pass the runtimes on the command line. Both short (`net8`) and full (`net8.0`) forms are accepted:

```bash
dotnet run -- --runtimes net8,net9,net10
dotnet run -- --runtimes net8.0,net10.0
dotnet run -- --runtimes net8,net9 --iterations 500 --reporter markdown --output ./results
```

When `--runtimes` is specified, the host builds the project for each target framework via `dotnet build -f <tfm>`, runs the benchmarks in a child process under that runtime, and aggregates the results.

## Harness mode: `[Runtimes]` attribute

Instead of passing `--runtimes` on the CLI, you can declare the runtimes on the benchmark class itself:

```csharp
using NBenchmark.Attributes;

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)]
public class StringBenchmarks
{
    [Benchmark]
    public string Concat() => "a" + "b" + "c";
}
```

```bash
# No --runtimes flag needed - the attribute drives the build
dotnet run --project samples/MultiRuntimeHarness
```

### How `--runtimes` and `[Runtimes]` interact

When `--runtimes` is passed on the CLI, the CLI list wins and `[Runtimes]` is ignored. When multiple classes declare `[Runtimes]`, the host uses the union of all declared lists (preserving declaration order, deduplicating). A class filtered out by `--filter` does not contribute its runtimes.

| `--runtimes` flag | `[Runtimes]` attribute | Runtimes used |
|-------------------|------------------------|---------------|
| absent            | absent                 | none (single-runtime) |
| absent            | present on >= 1 class  | union of all declared lists |
| present           | absent or present      | CLI list; attribute ignored |

## How it works

`WithRuntimes` and `--runtimes` implicitly enable process isolation: each runtime runs in a freshly spawned child process via `dotnet exec`, so JIT, GC, and thread-pool state from one runtime cannot bias another. `--runtimes` overrides `--in-process`; cross-runtime always uses child processes.

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance testing is performed within each runtime (net8 results are compared against the net8 baseline, not the net10 one). The first runtime in the list is the implicit baseline for ratio calculations.

## Samples

- [MultiRuntimeSuite sample](../samples.md#multiruntimesuite---suite-mode-multi-runtime) - Suite mode multi-runtime
- [MultiRuntimeHost sample](../samples.md#multiruntimehost---harness-mode-multi-runtime) - Harness mode multi-runtime

## See also

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - the underlying process isolation model
- [CLI reference](../reference/cli.md) - all `BenchmarkHarness` flags