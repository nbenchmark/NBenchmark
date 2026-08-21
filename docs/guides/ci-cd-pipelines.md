---
title: Tuning for CI/CD pipelines
description: Get clean numbers on a noisy shared runner and fail the build on regression - isolation, environment control, the threshold gate, and launch-count as the honest signal.
order: 2
---

# Tuning for CI/CD pipelines

## Scenario

When you run your benchmark suite on a shared CI runner, other builds may steal CPU cycles, the disk may thrash, and memory can be contested. This often leads to inconsistent results: your local numbers are clean, but the CI environment reports different medians for each run - sometimes varying by as much as 30%. Consequently, the significance column may flip between **✓** and **✗** across commits that did not modify the code under test.

To resolve this, you need to:
1. Reduce noise at the source.
2. Obtain an honest signal when noise is unavoidable.
3. Fail the build only when a regression is statistically real.

Think of this as measuring an object in a noisy room. You can shut the doors and turn off the air conditioning (environment control), place the scale under a glass dome (process isolation), or accept the noise and weigh the object many times to estimate the spread (launch count). You cannot trust a single measurement taken during a hurricane.

## Complete example

The following `BenchmarkHarness` configuration reduces noise and enforces a performance gate:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithHardwareAffinity(2, 3)
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithDedicatedHostGuidance()
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

Use the following CLI invocation for a full CI run:

```bash
dotnet run -c Release -- \
  --cpu-affinity 2,3 \
  --priority high \
  --dedicated-host-guidance \
  --launch-count 5 \
  --threshold-pct 10 \
  --reporter json --output ./benchmark-results
```

## What's happening

- **Isolated runs**: By default, harness mode runs each discovered class in its own freshly spawned worker process. This ensures that JIT, GC, and thread-pool state from one class cannot bias another. If a specific benchmark requires its own clean environment, add the `[IsolatedProcess]` attribute to that method. For more information, see [Isolated runs](../features/isolated-runs.md).

- **CPU affinity** (`--cpu-affinity 2,3`): This pins the benchmark process to specific cores, preventing the OS scheduler from migrating the thread to a cold-cache core mid-measurement. It is recommended to choose cores other than core 0, as that core typically handles OS driver interrupts. This setting propagates to isolated workers automatically.

- **Process priority** (`--priority high`): This reduces preemption by unrelated OS tasks. If the runner denies the priority elevation (which is common on locked-down runners), the engine issues a warning, but the run proceeds at the priority allowed by the host. The priority is restored once the run completes.

- **Dedicated-host guidance** (`--dedicated-host-guidance`): This is a non-fatal pre-run probe that warns you if the host appears noisy (e.g., low core count, unraisable priority, or macOS thermal/frequency scaling). It provides guidance rather than acting as a gate. For more information, see [Environment control](../features/environment-control.md).

- **Launch count** (`--launch-count 5`): The engine runs each benchmark five times as independent launches and reports the cross-launch aggregation. On a contested host, per-launch medians will likely disagree. This disagreement is the "honest signal" that indicates noise is present. The reported number is the average across launches, the interval represents the spread between them, and significance is computed using samples pooled from all launches. For more information, see [Multiple launches](../features/multiple-launches.md).

- **Threshold gate** (`--threshold-pct 10`): After collecting all results, the harness compares the median of each non-baseline result against the baseline. If any result exceeds `baseline * (1 + 10/100)`, the harness sets `Environment.ExitCode = 1` and prints the names of the regressed benchmarks to stderr. In multi-runtime mode, the check is grouped within each runtime. For more information, see the [CLI reference](../reference/cli.md).

> [!IMPORTANT] Order of operations
> Isolation and environment control reduce noise at the source, and the threshold gate then decides based on those cleaned numbers. If you use `--threshold-pct` without noise reduction, you may encounter false positives on shared runners because the gate will fire on noise rather than actual regressions. Always pair the gate with isolation, environment control, or `--launch-count`.

## Run the benchmark

### Local development
On a quiet development machine, use these flags for faster iteration:

```bash
# Sanity check: fast, in-process, no gate
dotnet run -c Release -- --in-process --auto-tune quick
```

### CI runner
On a CI runner, use the full noise-reduction stack:

```bash
dotnet run -c Release -- \
  --cpu-affinity 2,3 --priority high --dedicated-host-guidance \
  --launch-count 5 \
  --threshold-pct 10 \
  --reporter json --output ./benchmark-results
```

Use `--in-process` only for local speed; never use it for a CI gate. Similarly, avoid `--auto-tune quick` in CI, as it shortens the run but loosens the target.

## Read the results

When using a noisy runner, monitor these three indicators:

1. **The auto-tune line (Advanced detail)**: If `jitter` is above 0.10, the host is contested. In this case, the engine automatically switches the outlier detector from an IQR fence to MAD. This is expected on shared runners.
2. **The launch aggregation table (when `--launch-count > 1`)**: If per-launch medians span a wide range, this variance is the primary finding. A single launch would have reported one of those medians with a tight error bar, which would be misleading.
3. **The threshold check**: If the process exits with a non-zero code, stderr will list the regressed benchmarks. If it exits with zero, no benchmark regressed beyond the 10% threshold.

A tight error column next to a `maxCeiling` stop on a shared runner does not necessarily mean the measurement converged. The CI-width stop rule runs on the raw stream, while the error is computed on the trimmed set. Check the `autoTune.sampleStop` field before trusting the margin. For more information, see [Raw vs. trimmed statistics](../statistics/measurement.md#raw-vs-trimmed-statistics) and the [Troubleshooting guide](../troubleshooting.md).

## GitHub Actions snippet

The following minimal job runs benchmarks on a quiet runner and fails the build upon regression:

```yaml
name: Benchmarks
on: [pull_request]
jobs:
  benchmark:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet build -c Release ./benchmarks/MyApp.Benchmarks.csproj
      - run: dotnet run --project ./benchmarks/MyApp.Benchmarks -c Release --no-build -- \
          --cpu-affinity 2,3 --priority high --dedicated-host-guidance \
          --launch-count 5 --threshold-pct 10 \
          --reporter json --output ./benchmark-results
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: benchmark-results
          path: ./benchmark-results
```

> [!TIP]
> GitHub-hosted runners are shared-tenant VMs. You should expect `--dedicated-host-guidance` to warn about low effective isolation. For a publication-grade gate, use a self-hosted runner with `--cpu-affinity` on dedicated cores. If you prefer to embed thresholds in your unit tests instead of using `--threshold-pct`, use the `MaxAbsoluteThresholdTolerance` setting in the [test-integration packages](../test-integration/index.md).

## Next steps

For more information, see the following pages:

- [Environment control](../features/environment-control.md) - Detailed information on CPU affinity, process priority, and dedicated-host guidance.
- [Isolated runs](../features/isolated-runs.md) - Per-class vs. per-benchmark isolation and the worker dispatch model.
- [Multiple launches](../features/multiple-launches.md) - Cross-launch aggregation and the `[Benchmark(LaunchCount = n)]` attribute.
- [Configuration: AutoTune](../reference/configuration.md#autotune) - When to use the `Quick`, `Default`, or `Thorough` presets.
- [Performance gates in your test suite](./performance-gates.md) - An alternative to `--threshold-pct` for projects that already run a unit test suite in CI.
- [Troubleshooting](../troubleshooting.md) - A symptom-to-fix index for noisy CI environments.
