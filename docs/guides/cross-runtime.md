---
title: Cross-runtime comparison
description: Verify your code benefits from net10 vs net8 with multi-runtime comparison, always-child isolation, and significance grouped within each runtime.
order: 5
---

# Cross-runtime comparison

## Scenario

You support net8, net9, and net10. You want to know whether the net10 runtime delivers a real speedup for your hot paths, or whether you should hold off recommending the upgrade. NBenchmark builds the same benchmarks for each target framework, measures each build in its own worker process, stamps every result with its `RuntimeMoniker`, and groups significance within each runtime so net8 is never compared against the net10 baseline.

## Complete example

### Project setup

The project must target all the runtimes you want to compare:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NBenchmark" />
    <PackageReference Include="NBenchmark.Reporters.Console" />
  </ItemGroup>
</Project>
```

### Suite mode - `WithRuntimes`

```csharp
var results = await new BenchmarkSuite("string-concat")
    .Add("concat", () => "a" + "b" + "c" + "d" + "e")
    .Add("interpolate", () => $"a {"b"} {"c"} {"d"} {"e"}")
    .Add("join", () => string.Join("", "a", "b", "c", "d", "e"))
    .WithBaseline("concat")
    .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

### Harness mode - `--runtimes` or `[Runtimes]`

On the CLI:

```bash
dotnet run -c Release -- --runtimes net8,net9,net10
```

Or declared on the class (no `--runtimes` flag needed - the attribute drives the build):

```csharp
[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)]
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c" + "d" + "e";

    [Benchmark]
    public string Interpolate() => $"a {"b"} {"c"} {"d"} {"e"}";
}
```

When `--runtimes` is passed on the CLI, the CLI list wins and `[Runtimes]` is ignored. When multiple classes declare `[Runtimes]`, the host uses the union of all declared lists.

## What's happening

- **`WithRuntimes(...)` / `--runtimes net8,net9,net10` / `[Runtimes(...)]`** - the three ways to trigger cross-runtime execution. Each runtime builds via `dotnet build -f <tfm>` and is measured in that build's own worker. In Suite mode this needs a `[BenchmarkPlan]` factory, because a suite's bodies are addressed by metadata token and a token from one build means nothing in another. See [Multi-runtime comparison](../features/multi-runtime.md).

- **Cross-runtime always isolates**, regardless of `--in-process` / `WithIsolation` settings. Each runtime is a clean CLR with no JIT, GC or thread-pool state warmed by siblings. This is non-negotiable: a comparison across runtimes measured in one process is not a comparison across runtimes.

- **Significance is grouped within each runtime.** net8 results are compared against the net8 baseline, not the net10 one. Cross-runtime significance is not computed because a cross-runtime comparison is not a like-for-like comparison of your code - it conflates your code's behavior with the runtime's behavior.

- **The first runtime in the list is the implicit baseline** for ratio calculations within that runtime. Use `WithBaseline` (Suite) or `[Benchmark(Baseline = true)]` (Harness) to designate the benchmark that's the 1.00x reference; the runtime order controls which runtime's results are presented first.

- **Environment controls propagate to the children.** `--cpu-affinity`, `--priority`, and `--dedicated-host-guidance` apply to each spawned child, so every runtime runs under the same hardware constraints. See [Environment control: Isolated-process propagation](../features/environment-control.md#isolated-process-propagation).

> [!IMPORTANT] Compare on the same host
> Cross-runtime comparisons are only meaningful when the runtimes run on the same machine in the same conditions. Don't compare net8 results from your laptop against net10 results from CI - the host difference will dwarf the runtime difference. Run all three runtimes in the same invocation, on the same runner, with the same environment controls.

## Run it

```bash
# Suite mode
dotnet run -c Release

# Harness mode, CLI-driven
dotnet run -c Release -- --runtimes net8,net9,net10

# A subset, with publication-grade precision
dotnet run -c Release -- --runtimes net8,net10 --auto-tune thorough --reporter markdown --output ./results

# Attribute-driven (no --runtimes flag needed)
dotnet run -c Release --project samples/MultiRuntimeHarness
```

## Read the results

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance and ratio are computed within each runtime:

```text
string-concat
Benchmark   | Runtime | Median    | Mean      | Ops/s      | Ratio             | Sig | Mag    | Alloc/op
------------+---------+-----------+-----------+------------+-------------------+-----+--------+---------
concat      | net8.0  |  18.2 ns  |  18.4 ns  | 54,945,055 | baseline          |  -  |  -      |    32 B
interpolate | net8.0  |  17.9 ns  |  18.1 ns  | 55,248,618 | 0.98x             |  ✗  | neg    |    32 B
concat      | net10.0 |  15.1 ns  |  15.3 ns  | 66,225,165 | baseline          |  -  |  -      |    32 B
interpolate | net10.0 |  14.2 ns  |  14.4 ns  | 69,444,444 | 0.94x             |  ✓  | small  |    32 B
```

Reading this:

- **Within net8.0**, `interpolate` is not significantly different from `concat` (`✗`, `neg` magnitude).
- **Within net10.0**, `interpolate` is significantly faster (`✓`, `small` magnitude, `0.94x`).
- **Across runtimes**, `concat` itself went from 18.2 ns (net8) to 15.1 ns (net10) - a ~17% improvement from the runtime alone. The runtime upgrade is the larger effect; the algorithm choice within net10 is smaller but real.

The within-runtime significance is the authoritative signal. Do not read the cross-runtime medians as a significance verdict - they're presented for comparison, not tested.

See [Reading Your Results](../output/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Multi-runtime comparison](../features/multi-runtime.md) - the full model, including how `--runtimes` and `[Runtimes]` interact, the build / DLL-location / cleanup lifecycle, and the moniker-to-TFM mapping.
- [Isolated runs](../features/isolated-runs.md) - the underlying process-isolation model that cross-runtime execution builds on.
- [Environment control](../features/environment-control.md) - controls that propagate to every spawned child so each runtime runs under the same hardware constraints.
- [Samples: MultiRuntimeSuite](../samples.md#multiruntimesuite---suite-mode-multi-runtime) and [MultiRuntimeHarness](../samples.md#multiruntimehost---harness-mode-multi-runtime) - runnable sample projects.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - the noise-reduction stack to apply when running cross-runtime in CI, where the host difference can dwarf the runtime difference.
