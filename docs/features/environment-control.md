---
title: Environment control
description: Pin benchmarks to CPU cores, raise process priority, and detect noisy hosts to reduce measurement noise at its source.
order: 8
---

# Environment control

NBenchmark's [outlier trimming](../statistics/outliers.md) and [bimodal warning](../statistics/outliers.md#bimodal-distribution-warning) react to measurement noise after the fact - they discard or flag samples that look like OS interference. **Environment control** is the proactive counterpart: it reduces noise at the source before the timer starts.

Three opt-in controls are available. All default to off, all are restored when the run completes, and none are required for the zero-ceremony "just run my benchmark" path.

## CPU affinity

Pin the benchmark process to specific logical CPU cores to eliminate inter-core migration noise. When the OS scheduler moves a benchmark thread between cores, the cold L1/L2 cache on the new core inflates a handful of samples; pinning keeps the thread on one core so the cache stays warm.

```csharp
// Suite / Harness fluent API
new BenchmarkSuite("MySuite")
    .WithHardwareAffinity(2, 3)
    .Add(...)
    .RunAsync();

await BenchmarkHarness.Create(args)
    .WithHardwareAffinity(2, 3)
    .RunAsync();
```

```bash
# CLI
dotnet run -- --cpu-affinity 2,3
```

Core indices are zero-based and logical (as reported by the OS). The prior affinity mask is restored when the run completes.

**Choosing cores:** core 0 is often used by the OS for driver interrupt handling on Linux and Windows; avoid it for single-core pinning. A small group away from core 0 (e.g. `2,3` on an 8-core host) is the typical sweet spot for single-threaded benchmarks: it avoids the OS core and gives the scheduler room to honour affinity without starving the benchmark.

**Platform support:** processor affinity is applied on Linux and Windows. On macOS the BCL does not expose the `setaffinity` syscall, so the flag is accepted but skipped with a warning. Pin to a Linux or Windows host for affinity-pinned CI gates.

## Process priority

Request a higher process priority to reduce preemption by unrelated OS work. On a busy host, normal-priority benchmark threads compete with every other process for CPU time; each preemption adds a multi-millisecond stall to a sample that has nothing to do with your code.

```csharp
new BenchmarkSuite("MySuite")
    .WithProcessPriority(ProcessPriorityClass.High)
    .Add(...)
    .RunAsync();
```

```bash
dotnet run -- --priority high
```

`high` is the recommended value for dedicated benchmark hosts. `realtime` can starve the OS and is discouraged.

A refused elevation (common on locked-down CI runners that disallow priority changes) is surfaced as a console warning, not an error - the run still proceeds at whatever priority the host allows. The prior priority is restored when the run completes.

## Dedicated-host guidance

A non-fatal pre-run probe that warns when the host looks like a shared or otherwise noisy benchmark environment. Enable it on CI runners and dev laptops to surface hidden noise sources before you trust a comparison.

```bash
dotnet run -- --dedicated-host-guidance
```

The probe checks for:

- **Low CPU core count** (< 4 logical cores) - typical of shared-tenant CI runners. Inflates noise and makes baseline comparisons unreliable.
- **macOS** - frequency scaling and thermal throttling are not directly observable from managed code. The probe suggests running on wall power and preferring a dedicated Linux or Windows host for CI gates.
- **Priority not raised on a suitable host** (>= 4 cores, no `--priority` set) - the probe actively suggests `--priority high` (or `WithProcessPriority`) to reduce preemption.

The run still proceeds regardless of what the probe finds - this is guidance, not a gate.

```csharp
new BenchmarkSuite("MySuite")
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Combining the controls

The three controls are independent and compose. For a dedicated benchmark host running a CI regression gate:

```bash
dotnet run -- --cpu-affinity 2,3 --priority high --dedicated-host-guidance
```

In code:

```csharp
var options = new MeasurementOptions
{
    Environment = new EnvironmentOptions
    {
        CpuAffinity = [2, 3],
        ProcessPriority = ProcessPriorityClass.High,
        DedicatedHostGuidance = true,
    },
};
```

The fluent methods layer on top of each other, so you can chain them:

```csharp
new BenchmarkSuite("MySuite")
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithHardwareAffinity(2, 3)
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Isolated-process propagation

In [Harness mode](../usage-modes/harness-mode.md) the host runs each discovered class in a child process by default. Environment controls are propagated to those children via the isolated-run request, so each child pins itself to the same cores and priority as the parent - the clean-room CLR runs under the same hardware constraints as the parent's in-process benchmarks.

Suite-mode isolation (`WithIsolation()`) re-runs the entry point in the child, so the child re-derives the same `MeasurementOptions` (including `Environment`) and applies it itself. No extra wiring is needed.

See [Isolated runs](./isolated-runs.md) for the full isolation model.

## What this is not

Environment control reduces noise; it does not eliminate it. The [adaptive measurement loop](../statistics/measurement.md) and [outlier trimming](../statistics/outliers.md) still run and still matter - they handle the residual noise that makes it through even a pinned, elevated process. Think of environment control as raising the floor on measurement quality, not as a replacement for the statistical machinery.

For a discussion of why benchmarking on a noisy host is fundamentally hard, see the [Troubleshooting guide](../troubleshooting.md).

## See also

- [Configuration: Environment](../reference/configuration.md#environment) - the `EnvironmentOptions` record reference
- [CLI Reference](../reference/cli.md) - `--cpu-affinity`, `--priority`, `--dedicated-host-guidance`
- [Measurement](../statistics/measurement.md) - the adaptive loop that runs under these controls
- [Outlier Trimming](../statistics/outliers.md) - the reactive noise handling this complements