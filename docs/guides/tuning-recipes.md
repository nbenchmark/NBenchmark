---
title: Tuning recipes
description: Configuration recipes for common situations - noisy CI, fast feedback, publication-grade precision, pure CPU measurement, and debugging unstable results.
order: 8
---

# Tuning recipes

Five configuration recipes for situations you are likely to hit. Each shows the code and the CLI
equivalent, explains why the settings work together, and links to the property definitions in the
[configuration reference](../reference/configuration.md).

## Tuning for noisy CI environments

**When to use:** Your benchmark runs on a shared CI runner (GitHub Actions, Azure Pipelines, etc.) where CPU cycles, memory bandwidth, and scheduler time are shared with other containers. Results are noisy and comparisons are unreliable.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Environment.ProcessPriority = High` | Reduces preemption by unrelated OS work. The benchmark thread is less likely to be paused mid-sample - on Windows the measuring thread's own priority is raised to match. |
| Thread control (on by default) | The measuring thread takes the affinity you pinned rather than only the process taking it, so the runtime's own threads stop competing for the same core. Leave it on. |
| Evidence-based interference rejection (on by default) | This is the setting that matters most on a shared runner: it discards a sample the OS is *known* to have preempted (the measuring thread's own CPU occupancy, not an inference from the timing) before the statistical detector runs. Leave it on; see [Evidence-based interference rejection](../statistics/outliers.md#evidence-based-interference-rejection). |
| `OutlierMode.MedianAbsoluteDeviation` | More robust than the default IQR fence for whatever preempted samples the evidence-based filter did not have enough signal to catch. MAD has a 50% breakdown point. |
| `.WithLaunchCount(3)` | Runs the benchmark 3 times as independent launches, one worker process each. The reported number is the average across them and the interval is the spread between them, so it describes reproducibility rather than one process's precision. |
| `AutoTune.CapBehavior = Error` | If the wall-clock cap is hit before the CI target is met, the benchmark errors instead of silently reporting a wide interval. |
| The drift canary (on by default) | A shared runner's speed moves during a run. The canary times fixed work at each benchmark boundary, so a comparison smaller than the drift that separated its two rows is flagged instead of reported as a result. Leave it on here of all places. |

**Fluent API:**

```csharp
await new BenchmarkSuite("ci-suite")
    .Add("myBenchmark", () => MyMethod())
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithLaunchCount(3)
    .WithAutoTune(new AutoTuneOptions { CapBehavior = CapBehavior.Error })
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --priority high --outlier mad --launch-count 3 --autotune-cap-behavior error
```

**See also:**

- [Environment](../reference/configuration.md#environment)
- [OutlierMode](../reference/configuration.md#outliermode)
- [LaunchCount](../reference/configuration.md#launchcount)
- [AutoTune](../reference/configuration.md#autotune)
- [Environment Control](../features/environment-control.md)

---

## Fast feedback during development

**When to use:** You are iterating on code and need a quick signal - seconds, not minutes. Precision is less important than turnaround time.

**What to combine:**

| Setting | Why |
| --- | --- |
| `AutoTune = AutoTuneOptions.Quick` | Lowers the CI target to ±5%, reduces minimum samples to 15, and minimum warmup to 4. |
| `WarmupIterations = 4` | Pins a short warmup instead of auto-detecting. |
| `Iterations = 20` | Pins a small measured sample count. |
| `ConfidenceLevel = 0.90` | A 90% CI is narrower and requires fewer samples to satisfy. |

**Fluent API:**

```csharp
await new BenchmarkSuite("fast-feedback")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTunePreset.Quick)
    .WithWarmup(4)
    .WithIterations(20)
    .WithConfidenceLevel(0.90)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --auto-tune quick --warmup 4 --iterations 20 --confidence 0.90
```

**See also:**

- [AutoTune](../reference/configuration.md#autotune)
- [WarmupIterations](../reference/configuration.md#warmupiterations)
- [Iterations](../reference/configuration.md#iterations)
- [ConfidenceLevel](../reference/configuration.md#confidencelevel)

---

## Publication-grade precision

**When to use:** You are publishing benchmark results, comparing across commits in a blog post, or establishing a baseline that others will rely on. Accuracy matters more than run time.

**What to combine:**

| Setting | Why |
| --- | --- |
| `AutoTune = AutoTuneOptions.Thorough` | Raises the CI target to ±1%, minimum samples to 100, and minimum warmup to 16. |
| `ConfidenceLevel = 0.99` | A 99% CI is wider and more conservative. |
| `.WithLaunchCount(5)` | Multiple independent launches let you report cross-launch statistics and an interval that reflects run-to-run variance. |
| `EnableHistogram = true` | The latency histogram gives you the full distribution, not just summary statistics. |

**Fluent API:**

```csharp
await new BenchmarkSuite("publication")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTunePreset.Thorough)
    .WithConfidenceLevel(0.99)
    .WithLaunchCount(5)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --auto-tune thorough --confidence 0.99 --launch-count 5
```

**See also:**

- [AutoTune](../reference/configuration.md#autotune)
- [ConfidenceLevel](../reference/configuration.md#confidencelevel)
- [LaunchCount](../reference/configuration.md#launchcount)
- [EnableHistogram](../reference/configuration.md#enablehistogram)

---

## Pure CPU measurement

**When to use:** You want to measure CPU time only, excluding GC pressure, allocation overhead, and cache effects. Suitable for cryptographic algorithms, numeric kernels, and other CPU-bound work.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Profile = Independent` | Forces Gen0 GC before every iteration, full GC between benchmarks, and disables allocation tracking. |
| `OpsPerSample = 1` | Each sample is a single invocation. Calibration is skipped when per-iteration GC is on, so K stays 1 by default - pin it explicitly if you want a different value. |

**Fluent API:**

```csharp
await new BenchmarkSuite("cpu-only")
    .Add("myBenchmark", () => MyMethod())
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --profile independent
```

**See also:**

- [Profile](../reference/configuration.md#profile)
- [ForceGcBeforeEachIteration](../reference/configuration.md#forcegcbeforeeachiteration)
- [MeasureAllocations](../reference/configuration.md#measureallocations)
- [Measurement Profiles](../statistics/measurement.md#measurement-profiles)

---

## Debugging unstable results

**When to use:** Your benchmark produces wildly different numbers across runs, the Error column is large, or you see a bimodal-distribution warning and want to understand why.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Diagnostics = DiagnosticsOptions.All` | Enables GC collection counts, heap info, exception tracking, and CPU time. Lets you correlate timing spikes with GC pauses or CPU throttling. |
| `OutlierMode = MedianAbsoluteDeviation` | More robust to heavy-tailed distributions. If the default IQR fence is being distorted by a long tail, MAD gives a clearer picture. |
| `Detail = Advanced` | Shows the auto-tune diagnostic line (K, warmup, samples, CI half-width, jitter metric) and the outlier fence values. |
| `.WithLaunchCount(5)` or higher | The only setting that distinguishes "noisy measurement" from "number that does not reproduce". Without replicates across processes the interval describes one process's precision, and no other knob changes that. |

**Fluent API:**

```csharp
await new BenchmarkSuite("debug")
    .Add("myBenchmark", () => MyMethod())
    .WithDiagnostics(DiagnosticsMode.All)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithLaunchCount(5)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --diagnostics all --outlier mad --detail advanced --launch-count 5
```

**What to look for:**

- High jitter metric (> 0.10) in the auto-tune diagnostic: the host is noisy. Consider [environment controls](../features/environment-control.md).
- On an Apple Silicon Mac, a note that the quality-of-service class could not be raised: the measurement is running on a thread macOS will not let NBenchmark place, so the scheduler may put it on an efficiency core several times slower. See [macOS and Apple Silicon](../features/environment-control.md#macos-and-apple-silicon).
- GC collection counts that correlate with slow samples: GC pressure is affecting your timings. Try `--profile independent`.
- A bimodal-distribution warning: investigate the cause (lock contention, cache misses, GC pauses) rather than silencing it.
- A message naming samples "confirmed preempted by the OS": [evidence-based interference rejection](../statistics/outliers.md#evidence-based-interference-rejection) found direct proof, not an inference - a high rejected fraction means the host itself is too noisy to trust right now, not that your code is unstable.
- `autoTune.interferenceDisabledReason` set on Advanced detail: the filter could not run for this benchmark (unsupported platform, too expensive relative to the sample duration, or an async body whose continuations mostly hopped threads) - the timings are unaffected, but the OS-preemption evidence this section relies on is unavailable.
- A host-drift warning on a row: the machine moved further between that row and the baseline than the difference between them. See [The host drift canary](../statistics/measurement.md#the-host-drift-canary).
- `outliersRemoved` as a fraction of the sample count: this is how much of the run the fence threw away, and it is now visible in the Error column too - a run that trimmed heavily reports a wider margin than one that trimmed nothing, because [a discarded sample still counts as an observation](../statistics/outliers.md).

If each individual run reports a *tight* interval and only the runs disagree with each other, the cause is not noise and none of the above will find it. Three separate things produce that, each with its own field:

- `autoTune.warmupTimeFloorMet` is `false` - warmup ended before tiered compilation finished, so the body was measured on unoptimized code.
- `autoTune.sampleQuantizationFraction` exceeds the reported margin - the measurement is finer than the timer can resolve, so the digits describe the clock's step grid. See [Timer resolution](../statistics/measurement.md#timer-resolution).
- Neither, and `launchStatistics.processVarianceRatio` is high - the number genuinely does not reproduce that precisely. Nothing inside one process can fix this; see [Reading the reproducibility warning](../features/multiple-launches.md#reading-the-reproducibility-warning).

**See also:**

- [Diagnostics](../reference/configuration.md#diagnostics)
- [OutlierMode](../reference/configuration.md#outliermode)
- [LaunchCount](../reference/configuration.md#launchcount)
- [Reading Your Results](../getting-started/reading-your-results.md)
- [Troubleshooting](../troubleshooting.md#same-benchmark-a-different-median-each-run-and-every-run-reports-a-tight-error)

---
