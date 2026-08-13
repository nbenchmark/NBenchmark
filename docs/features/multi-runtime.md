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

When `--runtimes` is specified, the coordinator builds the project for each target framework via `dotnet build -f <tfm>`, measures the benchmarks in **that build's own worker process**, and aggregates the results. A worker is framework-dependent, so only the net8.0 worker can load a net8.0 build - the build targets already deploy the right one beside each build's assemblies, which makes worker selection a lookup rather than a guess.

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

`WithRuntimes` and `--runtimes` always isolate: each runtime is measured in a freshly spawned worker, so JIT, GC and thread-pool state from one runtime cannot bias another. `--runtimes` overrides `--in-process`, because a comparison across runtimes measured in one process would not be a comparison across runtimes at all.

In **Suite mode**, multi-runtime needs a `[BenchmarkPlan]` factory rather than an inline suite:

```csharp
await BenchmarkSuite.RunPlanAsync(BuildSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildSuite() =>
    new BenchmarkSuite("comparison")
        .Add("concat", () => Concat())
        .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net10);
```

Measuring another target framework means measuring a *different build* of your code, and a suite body is located by a build-specific address that changes when the code is recompiled. A factory is located by name, which is stable across builds, so each runtime's worker constructs the suite from that runtime's own assemblies. An inline suite with `WithRuntimes` says so rather than measuring the wrong thing. (For the addressing detail - how bodies are located across a process boundary, by build-specific token or by stable name - see [Isolation internals: by token, or by name](../deep-dives/isolation-internals.md#by-token-or-by-name).)

Harness mode needs no change: it already addresses benchmark classes by name.

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance testing is performed within each runtime (net8 results are compared against the net8 baseline, not the net10 one). The first runtime in the list is the implicit baseline for ratio calculations.

## Samples

- [MultiRuntimeSuite sample](../samples.md#multiruntimesuite---suite-mode-multi-runtime) - Suite mode multi-runtime
- [MultiRuntimeHarness sample](../samples.md#multiruntimeharness---harness-mode-multi-runtime) - Harness mode multi-runtime

## See also

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - the underlying process isolation model
- [CLI reference](../reference/cli.md) - all `BenchmarkHarness` flags
