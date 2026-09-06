---
title: Tuning recipes
description: Configuration recipes for common situations - noisy CI, fast feedback, publication-grade precision, pure CPU measurement, and debugging unstable results.
order: 8
---

# Tuning recipes

This page provides configuration recipes for common benchmarking scenarios. Each recipe includes the fluent API and CLI equivalents, an explanation of why the settings work together, and links to the [configuration reference](../reference/configuration.md).

## Tuning for noisy CI environments

**When to use**: Use this recipe when running benchmarks on a shared CI runner (such as GitHub Actions or Azure Pipelines). In these environments, CPU cycles, memory bandwidth, and scheduler time are shared with other containers, often leading to noisy results and unreliable comparisons.

**Recommended settings**:

| Setting | Purpose |
| --- | --- |
| `Environment.ProcessPriority = High` | Reduces preemption by unrelated OS tasks. This makes it less likely that the benchmark thread is paused mid-sample. On Windows, the measuring thread's priority is raised to match. |
| Thread control (enabled by default) | Ensures the measuring thread takes the pinned affinity rather than only the process. This prevents the runtime's own threads from competing for the same core. |
| Evidence-based interference rejection (enabled by default) | Discards samples that the OS is known to have preempted based on CPU occupancy rather than timing inference. See [Evidence-based interference rejection](../statistics/outliers.md#evidence-based-interference-rejection). |
| `OutlierMode.MedianAbsoluteDeviation` | Provides more robustness than the default IQR fence for preempted samples that the evidence-based filter misses. MAD has a 50% breakdown point. |
| `.WithLaunchCount(3)` | Runs the benchmark as three independent launches in separate worker processes. The reported number is the average across launches, and the interval describes reproducibility rather than single-process precision. |
| `AutoTune.CapBehavior = Error` | Causes the benchmark to error if the wall-clock cap is hit before the CI target is met, preventing the silent reporting of wide intervals. |
| The drift canary (enabled by default) | Monitors host speed shifts during a run by timing fixed work at benchmark boundaries. If the host drift is larger than the difference between two rows, the result is flagged. |

**Fluent API**:

```csharp
await new BenchmarkSuite("ci-suite")
    .Add("myBenchmark", () => MyMethod())
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithLaunchCount(3)
    .WithAutoTune(new AutoTuneOptions { CapBehavior = CapBehavior.Error })
    .RunAsync();
```

**CLI**:

```bash
dotnet run -- --priority high --outlier mad --launch-count 3 --autotune-cap-behavior error
```

**See also**:
- [Environment](../reference/configuration.md#environment)
- [OutlierMode](../reference/configuration.md#outliermode)
- [LaunchCount](../reference/configuration.md#launchcount)
- [AutoTune](../reference/configuration.md#autotune)
- [Environment Control](../features/environment-control.md)

---

## Fast feedback during development

**When to use**: Use this recipe when iterating on code and you need a quick signal in seconds rather than minutes. Turnaround time is prioritized over precision.

**Recommended settings**:

| Setting | Purpose |
| --- | --- |
| `AutoTune = AutoTuneOptions.Quick` | Lowers the CI target to ±5%, reduces minimum samples to 15, and minimum warmup to 4. |
| `WarmupIterations = 4` | Sets a short, fixed warmup instead of using auto-detection. |
| `Iterations = 20` | Sets a small, fixed measured sample count. |
| `ConfidenceLevel = 0.90` | Uses a 90% confidence interval, which is narrower and requires fewer samples. |

**Fluent API**:

```csharp
await new BenchmarkSuite("fast-feedback")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTuneOptions.Quick)
    .WithWarmup(4)
    .WithIterations(20)
    .WithConfidenceLevel(0.90)
    .RunAsync();
```

**CLI**:

```bash
dotnet run -- --auto-tune quick --warmup 4 --iterations 20 --confidence 0.90
```

**See also**:
- [AutoTune](../reference/configuration.md#autotune)
- [WarmupIterations](../reference/configuration.md#warmupiterations)
- [Iterations](../reference/configuration.md#iterations)
- [ConfidenceLevel](../reference/configuration.md#confidencelevel)

---

## Publication-grade precision

**When to use**: Use this recipe when publishing results, comparing across commits for a blog post, or establishing a baseline for others to rely on. Accuracy is prioritized over run time.

**Recommended settings**:

| Setting | Purpose |
| --- | --- |
| `AutoTune = AutoTuneOptions.Thorough` | Raises the CI target to ±1%, minimum samples to 100, and minimum warmup to 16. |
| `ConfidenceLevel = 0.99` | Uses a 99% confidence interval, which is wider and more conservative. |
| `.WithLaunchCount(5)` | Performs multiple independent launches to report cross-launch statistics and run-to-run variance. |
| `EnableHistogram = true` | Provides the full latency distribution rather than just summary statistics. |

**Fluent API**:

```csharp
await new BenchmarkSuite("publication")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTuneOptions.Thorough)
    .WithConfidenceLevel(0.99)
    .WithLaunchCount(5)
    .RunAsync();
```

**CLI**:

```bash
dotnet run -- --auto-tune thorough --confidence 0.99 --launch-count 5
```

**See also**:
- [AutoTune](../reference/configuration.md#autotune)
- [ConfidenceLevel](../reference/configuration.md#confidencelevel)
- [LaunchCount](../reference/configuration.md#launchcount)
- [EnableHistogram](../reference/configuration.md#enablehistogram)

---

## Pure CPU measurement

**When to use**: Use this recipe when you want to measure CPU time only, excluding GC pressure, allocation overhead, and cache effects. This is suitable for cryptographic algorithms, numeric kernels, and other CPU-bound work.

**Recommended settings**:

| Setting | Purpose |
| --- | --- |
| `Profile = Independent` | Forces a Gen0 GC before every iteration, a full GC between benchmarks, and disables allocation tracking. |
| `OpsPerSample = 1` | Sets each sample to a single invocation. Calibration is skipped when per-iteration GC is enabled, so the value stays 1 by default. |

**Fluent API**:

```csharp
await new BenchmarkSuite("cpu-only")
    .Add("myBenchmark", () => MyMethod())
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .RunAsync();
```

**CLI**:

```bash
dotnet run -- --profile independent
```

**See also**:
- [Profile](../reference/configuration.md#profile)
- [ForceGcBeforeEachIteration](../reference/configuration.md#forcegcbeforeeachiteration)
- [MeasureAllocations](../reference/configuration.md#measureallocations)
- [Measurement Profiles](../statistics/measurement.md#measurement-profiles)

---

## Debugging unstable results

**When to use**: Use this recipe when your benchmark produces widely different numbers across runs, the error column is large, or you see a bimodal-distribution warning.

**Recommended settings**:

| Setting | Purpose |
| --- | --- |
| `Diagnostics = DiagnosticsOptions.All` | Enables GC collection counts, heap info, exception tracking, and CPU time to correlate spikes with GC pauses or throttling. |
| `OutlierMode = MedianAbsoluteDeviation` | Provides more robustness for heavy-tailed distributions. Use this if the default IQR fence is distorted by a long tail. |
| `Detail = Advanced` | Displays the auto-tune diagnostic line (K, warmup, samples, CI half-width, jitter metric) and the outlier fence values. |
| `.WithLaunchCount(5)` or higher | Distinguishes "noisy measurements" from results that do not reproduce across processes. |

**Fluent API**:

```csharp
await new BenchmarkSuite("debug")
    .Add("myBenchmark", () => MyMethod())
    .WithDiagnostics(DiagnosticsOptions.All)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithLaunchCount(5)
    .RunAsync();
```

**CLI**:

```bash
dotnet run -- --diagnostics all --outlier mad --detail advanced --launch-count 5
```

### Indicators to monitor

- **High jitter metric (> 0.10)**: Shown in the auto-tune diagnostic. This indicates the host is noisy. Consider using [environment controls](../features/environment-control.md).
- **Quality-of-service class warnings (macOS)**: On Apple Silicon Macs, a note may appear stating the QoS class could not be raised. This means the scheduler may place the benchmark on an efficiency core. See [macOS and Apple Silicon](../features/environment-control.md#macos-and-apple-silicon).
- **GC correlation**: If GC collection counts correlate with slow samples, GC pressure is affecting timings. Try using `--profile independent`.
- **Bimodal-distribution warning**: Investigate the cause (such as lock contention, cache misses, or GC pauses) rather than silencing the warning.
- **Confirmed preemption**: If samples are "confirmed preempted by the OS," the host is too noisy to trust. This is detected via [evidence-based interference rejection](../statistics/outliers.md#evidence-based-interference-rejection).
- **Interference disabled**: If `autoTune.interferenceDisabledReason` is set, the filter could not run (e.g., unsupported platform or async body). Timings are unaffected, but OS-preemption evidence is unavailable.
- **Host-drift warning**: Indicates the machine speed moved significantly between the row and the baseline. See [The host drift canary](../statistics/measurement.md#the-host-drift-canary).
- **Trimmed fraction**: A high `outliersRemoved` fraction indicates the fence removed many samples. This is reflected in the Error column as a wider margin. See [discarded samples](../statistics/outliers.md).

### Analyzing non-reproducible results

If individual runs report tight intervals but the runs disagree with each other, check these three fields:

- **`autoTune.warmupTimeFloorMet` is `false`**: Warmup ended before tiered compilation finished, meaning the body was measured on unoptimized code.
- **`autoTune.sampleQuantizationFraction` exceeds the margin**: The measurement is finer than the timer resolution. See [Timer resolution](../statistics/measurement.md#timer-resolution).
- **`launchStatistics.processVarianceRatio` is high**: The result genuinely does not reproduce across processes. See [Reading the reproducibility warning](../features/multiple-launches.md#reading-the-reproducibility-warning).

**See also**:
- [Diagnostics](../reference/configuration.md#diagnostics)
- [OutlierMode](../reference/configuration.md#outliermode)
- [LaunchCount](../reference/configuration.md#launchcount)
- [Reading Your Results](../getting-started/reading-your-results.md)
- [Troubleshooting](../troubleshooting.md#median-differs-between-runs-with-tight-error)

---
