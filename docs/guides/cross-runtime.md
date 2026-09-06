---
title: Cross-runtime comparison
description: Verify your code benefits from net10 vs net8 with multi-runtime comparison, always-worker isolation, and significance grouped within each runtime.
order: 5
---

# Cross-runtime comparison

## Scenario

If you support multiple runtimes (such as .NET 8, .NET 9, and .NET 10), you may want to determine if a newer runtime provides a real speedup for your hot paths before recommending an upgrade. NBenchmark builds the same benchmarks for each target framework, measures each build in its own worker process, stamps every result with its `TargetFramework`, and groups significance within each runtime to ensure that a .NET 8 result is never compared against a .NET 10 baseline.

## Complete example

### Project setup

The project must target all the runtimes you want to compare. Update your `.csproj` file as follows:

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

### Suite mode: `WithRuntimes`

Use the `WithRuntimes` method to specify the target runtimes:

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

### Harness mode: `--runtimes` or `[Runtimes]`

You can specify runtimes using the CLI:

```bash
dotnet run -c Release -- --runtimes net8,net9,net10
```

Alternatively, declare the runtimes on the benchmark class using an attribute. This eliminates the need for the `--runtimes` flag, as the attribute drives the build process:

```csharp
[Runtimes("net8.0", "net9.0", "net10.0")]
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c" + "d" + "e";

    [Benchmark]
    public string Interpolate() => $"a {"b"} {"c"} {"d"} {"e"}";
}
```

If you provide the `--runtimes` flag on the CLI, it takes precedence and the `[Runtimes]` attribute is ignored. If multiple classes declare `[Runtimes]`, the host uses the union of all declared lists.

## What's happening

- **Multi-runtime triggers**: You can trigger cross-runtime execution via `WithRuntimes(...)`, `--runtimes net8,net9,net10`, or `[Runtimes(...)]`. The engine builds each runtime via `dotnet build -f <tfm>` and measures it in that build's own worker. In suite mode, this requires a `[BenchmarkPlan]` factory because suite bodies are addressed by metadata tokens, and a token from one build is invalid in another. For more information, see [Multi-runtime comparison](../features/multi-runtime.md).

- **Mandatory isolation**: Cross-runtime comparisons always use isolated workers, regardless of `--in-process` or `WithIsolation` settings. Each runtime is a clean CLR with no JIT, GC, or thread-pool state warmed by other runtimes. This is necessary because measuring different runtimes in a single process would not be a valid comparison.

- **Grouped significance**: Significance is computed within each runtime. For example, .NET 8 results are compared against the .NET 8 baseline, not the .NET 10 baseline. The engine does not compute cross-runtime significance because such a comparison conflates the behavior of your code with the behavior of the runtime.

- **Implicit baselines**: The first runtime in the list serves as the implicit baseline for ratio calculations within that runtime. Use `WithBaseline` (Suite) or `[Benchmark(Baseline = true)]` (Harness) to designate the 1.00x reference benchmark. The runtime order determines the presentation order of the results.

- **Environment control propagation**: Settings such as `--cpu-affinity`, `--priority`, and `--host-quality-warnings` apply to every spawned worker. This ensures that every runtime runs under the same hardware constraints. For more information, see [Environment control: Isolated-process propagation](../features/environment-control.md#isolated-process-propagation).

> [!IMPORTANT] Compare on the same host
> Cross-runtime comparisons are only meaningful when the runtimes execute on the same machine under identical conditions. Do not compare .NET 8 results from a laptop against .NET 10 results from a CI runner, as the host differences will outweigh the runtime differences. Run all runtimes in a single invocation on the same runner with the same environment controls.

## Run the benchmark

Execute the following commands based on your mode:

```bash
# Suite mode
dotnet run -c Release

# Harness mode, CLI-driven
dotnet run -c Release -- --runtimes net8,net9,net10

# Subset of runtimes with publication-grade precision
dotnet run -c Release -- --runtimes net8,net10 --auto-tune thorough --reporter markdown --output ./results

# Attribute-driven (no --runtimes flag needed)
dotnet run -c Release --project samples/MultiRuntimeHarness
```

## Read the results

When results span multiple runtimes, the console and markdown reporters add a **Runtime** column. Significance and ratio are computed within each runtime:

```text
string-concat
Benchmark   | Runtime | Median    | Mean      | Ops/s      | Ratio             | Sig | Mag    | Alloc/op
------------+---------+-----------+-----------+------------+-------------------+-----+--------+---------
concat      | net8.0  |  18.2 ns  |  18.4 ns  | 54,945,055 | baseline          |  -  |  -      |    32 B
interpolate | net8.0  |  17.9 ns  |  18.1 ns  | 55,248,618 | 0.98x             |  ✗  | neg    |    32 B
concat      | net10.0 |  15.1 ns  |  15.3 ns  | 66,225,165 | baseline          |  -  |  -      |    32 B
interpolate | net10.0 |  14.2 ns  |  14.4 ns  | 69,444,444 | 0.94x             |  ✓  | small  |    32 B
```

Interpret the results as follows:
- **Within .NET 8.0**: `interpolate` is not significantly different from `concat` (`✗`, `neg` magnitude).
- **Within .NET 10.0**: `interpolate` is significantly faster (`✓`, `small` magnitude, `0.94x`).
- **Across runtimes**: `concat` improved from 18.2 ns (.NET 8) to 15.1 ns (.NET 10), a ~17% improvement from the runtime upgrade alone. In this case, the runtime upgrade has a larger effect than the algorithm choice within .NET 10.

Within-runtime significance is the authoritative signal. Do not treat cross-runtime medians as a significance verdict; they are provided for comparison only.

For a full explanation of every column, indicator, and warning, see [Reading Your Results](../getting-started/reading-your-results.md).

## Next steps

For more information, see the following pages:

- [Multi-runtime comparison](../features/multi-runtime.md) - Full details on the build and cleanup lifecycle and the moniker-to-TFM mapping.
- [Isolated runs](../features/isolated-runs.md) - The underlying process-isolation model used for cross-runtime execution.
- [Environment control](../features/environment-control.md) - How settings propagate to workers to ensure consistent hardware constraints.
- [Samples: MultiRuntimeSuite](../samples.md#suite-mode-multi-runtime-sample) and [MultiRuntimeHarness](../samples.md#harness-mode-multi-runtime-sample) - Runnable sample projects.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - The noise-reduction stack to apply when running cross-runtime in CI.
