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

Pass the runtimes on the command line. The engine accepts both short (`net8`) and full (`net8.0`) forms:

```bash
dotnet run -- --runtimes net8,net9,net10
dotnet run -- --runtimes net8.0,net10.0
dotnet run -- --runtimes net8,net9 --iterations 500 --reporter markdown --output ./results
```

When you specify `--runtimes`, NBenchmark builds the project for each target framework via `dotnet build -f <tfm>`, measures the benchmarks in that build's own worker process, and aggregates the results. Because a worker is framework-dependent, only the net8.0 worker can load a net8.0 build. The build targets deploy the correct worker beside each build's assemblies, so worker selection is a simple lookup.

## Harness mode: `[Runtimes]` attribute

Instead of using the `--runtimes` flag, you can declare the runtimes on the benchmark class:

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
# No --runtimes flag is needed; the attribute drives the build
dotnet run --project samples/MultiRuntimeHarness
```

### Interaction between `--runtimes` and `[Runtimes]`

If you pass `--runtimes` on the CLI, the CLI list takes precedence and the `[Runtimes]` attribute is ignored. When multiple classes declare `[Runtimes]`, the engine uses the union of all declared lists (preserving declaration order and deduplicating). A class filtered out by `--filter` does not contribute its runtimes.

| `--runtimes` flag | `[Runtimes]` attribute | Runtimes used |
|-------------------|------------------------|---------------|
| absent            | absent                 | none (single-runtime) |
| absent            | present on >= 1 class  | union of all declared lists |
| present           | absent or present      | CLI list; attribute ignored |

## How it works

`WithRuntimes` and `--runtimes` always isolate. Each runtime is measured in a freshly spawned worker, ensuring JIT, GC, and thread-pool state from one runtime doesn't bias another. The `--runtimes` flag overrides `--in-process`, as a comparison across runtimes measured in a single process would be invalid.

In **Suite mode**, multi-runtime comparison requires a `[BenchmarkPlan]` factory instead of an inline suite:

```csharp
await BenchmarkSuite.RunPlanAsync(BuildSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildSuite() =>
    new BenchmarkSuite("comparison")
        .Add("concat", () => Concat())
        .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net10);
```

Measuring another target framework means measuring a different build of your code. A suite body is located by a build-specific address that changes during recompilation. However, a factory is located by name, which remains stable across builds. Each runtime's worker constructs the suite from that runtime's own assemblies. An inline suite with `WithRuntimes` throws rather than measuring the wrong thing. For more information on how bodies are located across a process boundary, see [Isolation internals: token and name addressing](../deep-dives/isolation-internals.md#token-and-name-addressing).

Harness mode requires no changes because it already addresses benchmark classes by name.

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance testing is performed within each runtime (net8 results are compared against the net8 baseline, not the net10 one). The first runtime in the list is the implicit baseline for ratio calculations.

## Samples

For complete working examples, see:

- [MultiRuntimeSuite sample](../samples.md#suite-mode-multi-runtime-sample) - Suite mode multi-runtime.
- [MultiRuntimeHarness sample](../samples.md#harness-mode-multi-runtime-sample) - Harness mode multi-runtime.

## See also

For more information, see the following pages:

- [Suite mode](../usage-modes/suite-mode.md) - The full fluent API.
- [Harness mode](../usage-modes/harness-mode.md) - Attribute-based discovery and CLI.
- [Isolated runs](./isolated-runs.md) - The underlying process isolation model.
- [CLI reference](../reference/cli.md) - All `BenchmarkHarness` flags.
