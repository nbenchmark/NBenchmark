---
title: Environment control
description: Pin benchmarks to CPU cores, raise process priority, and detect noisy hosts to reduce measurement noise at its source.
order: 8
---

# Environment control

NBenchmark's [outlier trimming](../statistics/outliers.md) and [bimodal warning](../statistics/outliers.md#bimodal-distribution-warning) react to measurement noise after the fact - they discard or flag samples that look like OS interference. **Environment control** is the proactive counterpart: it reduces noise at the source before the timer starts.

Four controls are available. Three are opt-in and default to off; **thread control is on by default**. All are restored when the run completes, and none are required for the zero-ceremony "just run my benchmark" path.

Each control applies at one of two scopes. The *process* scope covers the measuring process and every thread in it, including the runtime's own; the *thread* scope covers only the thread the measurement loop runs on. They are siblings rather than alternatives - pinning a process does not stop its finalizer, background GC and JIT threads from sharing the pinned core, and on macOS the process-level call does not exist at all.

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

**Choosing cores:** core 0 is often used by the OS for driver interrupt handling on Linux and Windows; avoid it for single-core pinning. A small group away from core 0 (e.g. `2,3` on an 8-core host) is the typical sweet spot for single-threaded benchmarks: it avoids the OS core and gives the scheduler room to honor affinity without starving the benchmark.

Affinity is applied at **both** scopes: the process is pinned, and so is the thread running the measurement loop. The second is what keeps the benchmark off a core its own runtime threads are using.

**Platform support:** processor affinity is applied on Linux and Windows. On macOS neither the process nor the thread call exists, so the flag is accepted but skipped with a warning - see [macOS and Apple Silicon](#macos-and-apple-silicon) for what is applied there instead.

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

On Windows the measuring thread's priority is raised to match, so `--priority high` means the same thing at both scopes. On Linux it is not, and deliberately so: under the default `SCHED_OTHER` policy a thread priority does nothing, and applying one would report success for a control with no effect.

A refused elevation (common on locked-down CI runners that disallow priority changes) is surfaced as a console warning, not an error - the run still proceeds at whatever priority the host allows. The prior priority is restored when the run completes.

## Thread control (on by default)

The measuring thread takes an affinity matching `--cpu-affinity`, a priority matching `--priority` (Windows), and - on macOS - the `QOS_CLASS_USER_INTERACTIVE` quality-of-service class that asks the scheduler for a performance core.

It is on by default because it needs no configuration to be useful and the alternative is a Mac measured on whichever core the scheduler picked. Turn it off to measure under the host's default thread scheduling:

```bash
dotnet run -- --no-thread-control
```

```csharp
new BenchmarkSuite("MySuite")
    .WithThreadControl(false)
    .Add(...)
    .RunAsync();
```

## macOS and Apple Silicon

The host probe does not label every Mac a shared runner. It reads the performance/efficiency core split where the platform reports one, reports what it did about it, and notes that frequency scaling and thermal throttling stay unobservable from managed code.

On Apple Silicon the cores are not interchangeable. An M1 Max reports 10 logical CPUs, of which 8 are performance cores and 2 are efficiency cores several times slower. NBenchmark reads that split (`hw.nperflevels` and `hw.perflevelN.logicalcpu`) and reports it in the host guidance, so the machine is described rather than dismissed:

```
Dedicated-host guidance:
  - Apple Silicon detected (8 performance cores, 2 efficiency cores). The measurement thread is
    raised to user-interactive quality of service wherever macOS permits it, ...
```

**Where the elevation lands, precisely.** Darwin refuses a quality-of-service change on any thread that carries an explicit scheduling priority, and the .NET runtime gives one to every thread it creates - a thread-pool thread and a `new Thread` alike. The process main thread is the exception: it is created by the kernel, already carries the user-interactive class, and accepts the call. So the elevation applies to a measurement running on the main thread, and is refused for one running on the thread pool. A refusal is reported rather than assumed away:

```
Note: this measurement runs on a thread the runtime created, whose quality-of-service class macOS
will not let us change ... The class is left unspecified, which is eligible for a performance core
but not pinned to one.
```

An unspecified class is not a background one - it is eligible for a performance core, and the scheduler is free to use an efficiency core under load. That residual is what the [outlier machinery](../statistics/outliers.md) and the [bimodal warning](../statistics/outliers.md#bimodal-distribution-warning) are for.

Frequency scaling and thermal throttling remain unobservable from managed code on macOS. Run on wall power with minimal background load, and read the [host drift canary](../statistics/measurement.md) output, which measures how far the machine moved during the run.

## Dedicated-host guidance

A non-fatal pre-run probe that warns when the host looks like a shared or otherwise noisy benchmark environment. Enable it on CI runners and dev laptops to surface hidden noise sources before you trust a comparison.

```bash
dotnet run -- --dedicated-host-guidance
```

The probe checks for:

- **Low CPU core count** (< 4 logical cores) - typical of shared-tenant CI runners. Inflates noise and makes baseline comparisons unreliable.
- **macOS** - the performance/efficiency core split where the platform reports one, what was done about it, and the reminder that frequency scaling and thermal throttling are not directly observable from managed code. See [macOS and Apple Silicon](#macos-and-apple-silicon).
- **No CPU affinity pinned on a suitable host** (>= 4 cores, Linux or Windows, no `--cpu-affinity` set) - the probe suggests `--cpu-affinity 2,3` (or `WithHardwareAffinity(2, 3)`) to pin to cores away from core 0 and eliminate inter-core migration noise.
- **Priority not raised on a suitable host** (>= 4 cores, no `--priority` set) - the probe actively suggests `--priority high` (or `WithProcessPriority`) to reduce preemption.

The run still proceeds regardless of what the probe finds - this is guidance, not a gate.

```csharp
new BenchmarkSuite("MySuite")
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Build-configuration guidance (always on)

Separate from the three host controls above, NBenchmark emits a one-time warning when:

- The entry assembly is built in `Debug` configuration.
- A debugger is attached.

Those conditions can make timings non-production-representative (for example, reduced inlining/tiering behavior), so the warning is enabled by default in single, suite, and harness modes.

When measuring Debug behavior is intentional, suppress it with either of these knobs:

```csharp
new BenchmarkSuite("MySuite")
    .WithSuppressBuildConfigurationWarning()
    .Add(...)
    .RunAsync();

await BenchmarkHarness.Create(args)
    .WithSuppressBuildConfigurationWarning()
    .RunAsync();
```

```bash
NBENCHMARK_SUPPRESS_DEBUG_WARNING=1 dotnet run -- --filter MyBenchmarks.*
```

## Combining the controls

The controls are independent and compose. For a dedicated benchmark host running a CI regression gate:

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

In [Harness mode](../usage-modes/harness-mode.md) the host runs each discovered class in a worker by default. Environment controls are propagated to those workers via the isolated-run request, so each worker pins itself to the same cores and priority as the host - the clean-room CLR runs under the same hardware constraints as the host's in-process benchmarks.

A `[BenchmarkPlan]` suite builds itself inside the worker, so it derives the same `MeasurementOptions` (including `Environment`) there and applies them itself. No extra wiring is needed.

See [Isolated runs](./isolated-runs.md) for the full isolation model.

## What this is not

Environment control reduces noise; it does not eliminate it. The [adaptive measurement loop](../statistics/measurement.md) and [outlier trimming](../statistics/outliers.md) still run and still matter - they handle the residual noise that makes it through even a pinned, elevated process. Think of environment control as raising the floor on measurement quality, not as a replacement for the statistical machinery.

For a discussion of why benchmarking on a noisy host is fundamentally hard, see the [Troubleshooting guide](../troubleshooting.md).

## See also

- [Configuration: Environment](../reference/configuration.md#environment) - the `EnvironmentOptions` record reference
- [CLI Reference](../reference/cli.md) - `--cpu-affinity`, `--priority`, `--dedicated-host-guidance`, `--no-thread-control`
- [Measurement](../statistics/measurement.md) - the adaptive loop that runs under these controls
- [Outlier Trimming](../statistics/outliers.md) - the reactive noise handling this complements
