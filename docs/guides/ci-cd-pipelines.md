---
title: Tuning for CI/CD pipelines
description: Get clean numbers on a noisy shared runner and fail the build on regression - isolation, environment control, the threshold gate, and launch-count as the honest signal.
order: 2
---

# Tuning for CI/CD pipelines

## Scenario

You run your benchmark suite on a shared CI runner. Other builds steal CPU cycles, the disk thrashes, and memory is contested. Your local numbers look clean, but CI reports a different median every run - sometimes 30% apart - and the significance column flips between ✓ and ✗ across commits that didn't touch the code under test. You want to (a) reduce the noise at the source, (b) get an honest signal when you can't, and (c) fail the build only when a regression is real.

The mental model is a measurement in a noisy room: you can shut the doors and turn off the AC (environment control), put the scale under a glass dome (process isolation), or accept that the room is noisy and weigh the grain many times to estimate the spread (launch count). What you cannot do is weigh a single grain once during a hurricane and trust the number.

## Complete example

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithHardwareAffinity(2, 3)
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithDedicatedHostGuidance()
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

```bash
# The full CI invocation
dotnet run -c Release -- \
  --cpu-affinity 2,3 \
  --priority high \
  --dedicated-host-guidance \
  --launch-count 5 \
  --threshold-pct 10 \
  --reporter json --output ./benchmark-results
```

## What's happening

- **Isolated runs** (Harness mode default). Each discovered class runs in its own freshly spawned child process, so JIT, GC, and thread-pool state from one class cannot bias another. You don't configure anything - `BenchmarkHarness.Create(args)...RunAsync()` already isolates per class. For a single benchmark that needs its own clean room, add `[IsolatedProcess]` to that method. See [Isolated runs](../features/isolated-runs.md).

- **CPU affinity** (`--cpu-affinity 2,3`). Pins the benchmark process to specific cores so the OS scheduler cannot migrate the thread to a cold-cache core mid-measurement. Choose cores away from core 0 (OS driver interrupt handling). Propagates to isolated children automatically.

- **Process priority** (`--priority high`). Reduces preemption by unrelated OS work. A refused elevation (common on locked-down runners) is a warning, not an error - the run proceeds at whatever priority the host allows. Restored when the run completes.

- **Dedicated-host guidance** (`--dedicated-host-guidance`). A non-fatal pre-run probe that warns when the host looks noisy (low core count, unraisable priority, macOS thermal/frequency scaling). Guidance, not a gate - the run still proceeds. See [Environment control](../features/environment-control.md).

- **Launch count** (`--launch-count 5`). Runs each benchmark 5 times as independent launches and reports cross-launch aggregation. On a contested host the per-launch medians will disagree, and that disagreement **is the honest signal**: it tells you the noise is real, not hidden behind a single lucky launch. The best (lowest-median) launch is the representative result; its raw samples feed significance. See [Multiple launches](../features/multiple-launches.md).

- **Threshold gate** (`--threshold-pct 10`). After all results are collected, the harness compares each non-baseline result's median against the baseline. If any exceeds `baseline * (1 + 10/100)`, the harness sets `Environment.ExitCode = 1` and prints the regressed names to stderr. In multi-runtime mode the check is grouped within each runtime. See the [CLI reference](../reference/cli.md).

> [!IMPORTANT] Order matters
> Isolation and environment control reduce noise at the source. The threshold gate then decides on the cleaned numbers. If you add `--threshold-pct` without any noise reduction, expect false positives on a shared runner - the gate fires on noise, not regressions. Always pair the gate with at least one of: isolation, environment control, or `--launch-count` so the gate has something honest to decide on.

## Run it

Locally, on a quiet dev machine:

```bash
# Sanity check - fast, in-process, no gate
dotnet run -c Release -- --in-process --auto-tune quick
```

On the CI runner, with the full noise-reduction stack:

```bash
dotnet run -c Release -- \
  --cpu-affinity 2,3 --priority high --dedicated-host-guidance \
  --launch-count 5 \
  --threshold-pct 10 \
  --reporter json --output ./benchmark-results
```

`--in-process` is for local iteration speed; never use it for a CI gate. `--auto-tune quick` shortens the run for development but loosens the CI target - leave it off in CI.

## Read the results

On a noisy runner, look at three things:

1. **The auto-tune line** (Advanced detail). If `jitter` is above 0.10, the host is contested - the loop auto-switched the outlier detector from IQR fence to MAD. That's expected on a shared runner; the warning is informational.
2. **The launch aggregation table** (when `--launch-count > 1`). If the per-launch medians span a wide range, the variance is the finding. A single launch would have reported one of those medians with a tight error bar and looked authoritative.
3. **The threshold check**. If the run exits non-zero, the stderr output names the regressed benchmarks. If it exits zero, no benchmark regressed beyond 10% against its baseline.

A tight Error column next to a `maxCeiling` stop on a shared runner is **not** evidence the measurement converged. The CI-width stop rule ran on the raw stream; the Error is computed on the trimmed set. Read the `autoTune.sampleStop` field before the margin. See [Raw vs. trimmed statistics](../statistics/measurement.md#raw-vs-trimmed-statistics) and the [Troubleshooting guide](../troubleshooting.md).

## GitHub Actions snippet

A minimal job that runs the benchmarks on a quiet runner and fails the build on regression:

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
> GitHub-hosted runners are shared-tenant VMs. `--dedicated-host-guidance` will warn about low effective isolation; that's expected. For a publication-grade gate, run on a self-hosted runner with `--cpu-affinity` on dedicated cores. The `MaxAbsoluteThresholdTolerance` knob on the [test-integration packages](../test-integration/index.md) is the equivalent escape hatch when you embed thresholds in your unit tests instead of using `--threshold-pct`.

## When to go deeper

- [Environment control](../features/environment-control.md) - the full model for CPU affinity, process priority, dedicated-host guidance, and how they propagate to isolated children.
- [Isolated runs](../features/isolated-runs.md) - per-class vs. per-benchmark isolation, `[InProcess]` opt-out, the child-process dispatch model.
- [Multiple launches](../features/multiple-launches.md) - cross-launch aggregation, the `[Benchmark(LaunchCount = n)]` per-method attribute, and how launch count interacts with isolation.
- [Configuration: AutoTune](../reference/configuration.md#autotune) - the `Quick` / `Default` / `Thorough` presets and when to use each.
- [Performance gates in your test suite](./performance-gates.md) - the in-test alternative to `--threshold-pct`, for projects that already run a unit test suite in CI.
- [Troubleshooting](../troubleshooting.md) - the symptom-to-fix index for noisy CI environments.