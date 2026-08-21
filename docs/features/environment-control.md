---
title: Environment control
description: Pin benchmarks to CPU cores, raise process priority, and detect noisy hosts to reduce measurement noise at its source.
order: 8
---

# Environment control

NBenchmark's [outlier trimming](../statistics/outliers.md) and [bimodal warning](../statistics/outliers.md#bimodal-distribution-warning) react to measurement noise after it happens by discarding or flagging samples that look like OS interference. **Environment control** is the proactive counterpart: it reduces noise at the source before the timer starts.

Four controls are available. Three are opt-in and disabled by default; **thread control is enabled by default**. The engine restores all controls when the run completes. None of these controls are required for a simple "just run my benchmark" path.

Each control applies at one of two scopes:
- **Process scope**: Covers the measuring process and every thread in it, including the runtime's own threads.
- **Thread scope**: Covers only the thread where the measurement loop runs.

These scopes are complementary rather than alternatives. Pinning a process doesn't stop its finalizer, background GC, or JIT threads from sharing the pinned core. Additionally, the process-level call doesn't exist on macOS.

## CPU affinity

Pin the benchmark process to specific logical CPU cores to eliminate inter-core migration noise. When the OS scheduler moves a benchmark thread between cores, the cold L1/L2 cache on the new core inflates a handful of samples. Pinning keeps the thread on one core so the cache stays warm.

**Fluent API:**
```csharp
new BenchmarkSuite("MySuite")
    .WithHardwareAffinity(2, 3)
    .Add(...)
    .RunAsync();

await BenchmarkHarness.Create(args)
    .WithHardwareAffinity(2, 3)
    .RunAsync();
```

**CLI:**
```bash
dotnet run -- --cpu-affinity 2,3
```

Core indices are zero-based and logical (as reported by the OS). The engine restores the prior affinity mask when the run completes.

**Choosing cores:** Core 0 is often used by the OS for driver interrupt handling on Linux and Windows; avoid it for single-core pinning. A small group away from core 0 (such as `2,3` on an 8-core host) is typically the sweet spot for single-threaded benchmarks. This avoids the OS core and gives the scheduler room to honor affinity without starving the benchmark.

Affinity is applied at both scopes: the process is pinned, and the thread running the measurement loop is also pinned. This prevents the benchmark from running on a core that its own runtime threads are using.

**Platform support:** Processor affinity is applied on Linux and Windows. On macOS, neither the process nor the thread call exists, so the flag is accepted but skipped with a warning. For more information, see [macOS and Apple Silicon](#macos-and-apple-silicon).

## Process priority

Request a higher process priority to reduce preemption by unrelated OS work. On a busy host, normal-priority benchmark threads compete with every other process for CPU time. Each preemption adds a multi-millisecond stall to a sample that is unrelated to your code.

**Fluent API:**
```csharp
new BenchmarkSuite("MySuite")
    .WithProcessPriority(ProcessPriorityClass.High)
    .Add(...)
    .RunAsync();
```

**CLI:**
```bash
dotnet run -- --priority high
```

`high` is the recommended value for dedicated benchmark hosts. `realtime` is discouraged as it can starve the OS.

On Windows, the engine raises the measuring thread's priority to match the process priority, so `--priority high` applies at both scopes. On Linux, this is not the case. Under the default `SCHED_OTHER` policy, thread priority has no effect, and applying one would report success for a control with no actual impact.

If the engine cannot elevate priority (common on locked-down CI runners), it issues a console warning rather than an error, and the run proceeds at the priority allowed by the host. The engine restores the prior priority when the run completes.

## Thread control (enabled by default)

The measuring thread takes an affinity matching `--cpu-affinity`, a priority matching `--priority` (on Windows), and - on macOS - the `QOS_CLASS_USER_INTERACTIVE` quality-of-service class to request a performance core.

This is enabled by default because it requires no configuration to be useful and prevents Macs from being measured on whatever core the scheduler picks. Turn it off to measure under the host's default thread scheduling:

**CLI:**
```bash
dotnet run -- --no-thread-control
```

**Fluent API:**
```csharp
new BenchmarkSuite("MySuite")
    .WithThreadControl(false)
    .Add(...)
    .RunAsync();
```

## macOS and Apple Silicon

The host probe doesn't label every Mac a shared runner. It reads the performance/efficiency core split where the platform reports one and notes that frequency scaling and thermal throttling remain unobservable from managed code.

On Apple Silicon, cores are not interchangeable. An M1 Max reports 10 logical CPUs, of which 8 are performance cores and 2 are efficiency cores. NBenchmark reads this split (`hw.nperflevels` and `hw.perflevelN.logicalcpu`) and reports it in the host guidance:

```
Dedicated-host guidance:
  - Apple Silicon detected (8 performance cores, 2 efficiency cores). The measurement thread is
    raised to user-interactive quality of service wherever macOS permits it, ...
```

**Elevation details.** Darwin refuses a quality-of-service change on any thread that carries an explicit scheduling priority. The .NET runtime gives such a priority to every thread it creates, including thread-pool threads and `new Thread` instances. The process main thread is the exception: it's created by the kernel, already carries the user-interactive class, and accepts the call. Therefore, the elevation applies to measurements running on the main thread but is refused for those running on the thread pool. The engine reports these refusals:

```
Note: this measurement runs on a thread the runtime created, whose quality-of-service class macOS
will not let us change ... The class is left unspecified, which is eligible for a performance core
but not pinned to one.
```

An unspecified class is not a background one; it's eligible for a performance core, though the scheduler may use an efficiency core under load. This residual noise is handled by the [outlier machinery](../statistics/outliers.md) and the [bimodal warning](../statistics/outliers.md#bimodal-distribution-warning).

Frequency scaling and thermal throttling remain unobservable from managed code on macOS. To minimize these effects, run on wall power with minimal background load and monitor the [host drift canary](../statistics/measurement.md) output.

## Dedicated-host guidance

A non-fatal pre-run probe warns when the host appears to be a shared or noisy benchmark environment. Enable it on CI runners and dev laptops to find hidden noise sources before trusting a comparison.

**CLI:**
```bash
dotnet run -- --dedicated-host-guidance
```

The probe checks for:

- **Low CPU core count** (< 4 logical cores): Typical of shared-tenant CI runners. This inflates noise and makes baseline comparisons unreliable.
- **macOS**: The performance/efficiency core split and a reminder that frequency scaling and thermal throttling are not observable. See [macOS and Apple Silicon](#macos-and-apple-silicon).
- **Missing CPU affinity on a suitable host** (>= 4 cores, Linux or Windows, no `--cpu-affinity` set): The probe suggests `--cpu-affinity 2,3` (or `WithHardwareAffinity(2, 3)`) to eliminate inter-core migration noise.
- **Missing priority elevation on a suitable host** (>= 4 cores, no `--priority` set): The probe suggests `--priority high` (or `WithProcessPriority`) to reduce preemption.

The run proceeds regardless of the probe's findings.

**Fluent API:**
```csharp
new BenchmarkSuite("MySuite")
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Build-configuration guidance (enabled by default)

NBenchmark emits a one-time warning in the following conditions:

- The entry assembly is built in `Debug` configuration.
- A debugger is attached.

These conditions can make timings non-representative of production (for example, by reducing inlining or tiering). This warning is enabled by default in single, suite, and harness modes.

Suppress this warning if measuring Debug behavior is intentional:

**Fluent API:**
```csharp
new BenchmarkSuite("MySuite")
    .WithSuppressBuildConfigurationWarning()
    .Add(...)
    .RunAsync();

await BenchmarkHarness.Create(args)
    .WithSuppressBuildConfigurationWarning()
    .RunAsync();
```

**Environment variable:**
```bash
NBENCHMARK_SUPPRESS_DEBUG_WARNING=1 dotnet run -- --filter MyBenchmarks.*
```

## Combining the controls

The controls are independent and compose. For a dedicated benchmark host running a CI regression gate, use:

**CLI:**
```bash
dotnet run -- --cpu-affinity 2,3 --priority high --dedicated-host-guidance
```

**Fluent API:**
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

// Or chain fluent methods:
new BenchmarkSuite("MySuite")
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithHardwareAffinity(2, 3)
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Isolated-process propagation

In [Harness mode](../usage-modes/harness-mode.md), the host runs each discovered class in a worker by default. Environment controls are propagated to those workers via the isolated-run request. Each worker pins itself to the same cores and priority as the host, ensuring the isolated CLR runs under the same hardware constraints.

A `[BenchmarkPlan]` suite builds itself inside the worker, so it derives the same `MeasurementOptions` (including `Environment`) and applies them locally.

For more information, see [Isolated runs](./isolated-runs.md).

## What this is not

Environment control reduces noise but does not eliminate it. The [adaptive measurement loop](../statistics/measurement.md) and [outlier trimming](../statistics/outliers.md) still handle the residual noise that persists even in a pinned, elevated process. Environment control raises the floor on measurement quality; it does not replace the statistical machinery.

For a discussion on why benchmarking on a noisy host is fundamentally difficult, see the [Troubleshooting guide](../troubleshooting.md).

## See also

For more information, see the following pages:

- [Configuration: Environment](../reference/configuration.md#environment) - Reference for the `EnvironmentOptions` record.
- [CLI Reference](../reference/cli.md) - Details on `--cpu-affinity`, `--priority`, `--dedicated-host-guidance`, and `--no-thread-control`.
- [Measurement](../statistics/measurement.md) - The adaptive loop that runs under these controls.
- [Outlier Trimming](../statistics/outliers.md) - The reactive noise handling that complements environment control.
