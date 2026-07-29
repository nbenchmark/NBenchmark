# cross-runtime.md

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


---

# ci-cd-pipelines.md

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


---

# custom-statistics.md

---
title: Custom statistics
description: The built-in IQR/MAD outlier trimming or rank-based significance tests don't fit your domain - swap in a custom IOutlierDetector and a custom ISignificanceTest.
order: 7
---

# Custom statistics

## Scenario

NBenchmark's built-in outlier trimming (IQR fence by default, MAD on noisy hosts) and significance testing (Mann-Whitney U for two groups, Kruskal-Wallis for three or more) are designed for general-purpose benchmarking. They cover the common cases well, but they are not the only choices:

- **Latency SLOs** care about the tail, not the mean. Trimming the slow samples before computing statistics hides exactly the values a latency budget needs to see.
- **Fixed physical thresholds** ("any sample above 1 ms is a stall") don't adapt to the data's spread the way an IQR fence does.
- **Domain-specific rules** ("compare medians, not distributions") are simpler than a rank-based test and more interpretable for your team.
- **Bootstrap or Bayesian comparison** gives you a posterior over the difference rather than a single p-value.

Every statistical primitive in NBenchmark is pluggable. The built-in strategies all implement the same `IOutlierDetector` and `ISignificanceTest` interfaces, so you can swap in your own - or compose the built-in ones - without forking the engine.

## Complete example

### A custom outlier detector

A tail-preserving detector that keeps the fastest `fraction` of samples - useful for latency-SLO work where the slow tail is the signal, not noise to be trimmed:

```csharp
public sealed class KeepFastestDetector(double fraction) : IOutlierDetector
{
    public string Name => $"keep fastest {fraction * 100:0.#}%";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        var keep = (int)Math.Floor(sortedSamples.Length * fraction);

        if (keep <= 0 || keep >= sortedSamples.Length)
            return OutlierClassification.KeepAll(sortedSamples);

        return new OutlierClassification
        {
            Kept = sortedSamples[..keep],
            Discarded = sortedSamples[keep..],
            UpperFence = sortedSamples[keep],
        };
    }
}
```

### A custom significance test

A median-ratio rule that marks a result significant when the median differs by more than a threshold percentage. No p-value, no distributional assumption - just "is the median more than X% off the baseline?":

```csharp
public sealed class MedianRatioSignificanceTest(double thresholdPercent) : ISignificanceTest
{
    public string Name => $"median ratio (>{thresholdPercent:0.#}%)";

    public SignificanceReport Analyze(SignificanceContext context)
    {
        var baseline = Median(context.Baseline.Samples);
        var pairwise = new List<PairwiseComparison>();

        foreach (var candidate in context.Candidates)
        {
            var deltaPercent = Math.Abs(Median(candidate.Samples) / baseline - 1.0) * 100.0;
            var verdict = deltaPercent > thresholdPercent
                ? SignificanceVerdict.Significant
                : SignificanceVerdict.NotSignificant;

            pairwise.Add(new PairwiseComparison(
                candidate.Name,
                PValue: null,
                Verdict: verdict,
                Effect: new EffectSize(
                    Metric: "median-ratio",
                    Value: deltaPercent,
                    Magnitude: deltaPercent switch
                    {
                        < 5 => "neg",
                        < 15 => "small",
                        < 30 => "med",
                        _ => "large",
                    },
                    Direction: EffectDirection.None,
                    PracticalValue: Math.Min(1.0, deltaPercent / 100.0))));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }

    private static double Median(double[] samples)
    {
        var sorted = samples.OrderBy(x => x).ToArray();
        return sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0
            : sorted[sorted.Length / 2];
    }
}
```

### Wiring them in

In Suite mode:

```csharp
await new BenchmarkSuite("latency-slo")
    .Add("v1", () => CurrentImpl())
    .Add("v2", () => CandidateImpl())
    .WithBaseline("v1")
    .WithOutlierDetector(new KeepFastestDetector(0.90))
    .WithSignificanceTest(new MedianRatioSignificanceTest(thresholdPercent: 25))
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

In Single / Harness mode:

```csharp
new MeasurementOptions
{
    OutlierDetector = new KeepFastestDetector(0.90),
    SignificanceTest = new MedianRatioSignificanceTest(25),
}
```

## What's happening

- **`IOutlierDetector`** receives the sorted-ascending sample array and returns an `OutlierClassification` with `Kept`, `Discarded`, and optional `LowerFence` / `UpperFence`. The contract: never discard every sample (return `KeepAll` when your rule would empty the set), don't mutate the input, and return `Kept` sorted ascending. A custom `OutlierDetector` takes priority over `OutlierMode`. The detector's `Name` appears in the report header (`Outliers: ...`). See [Outlier Trimming: Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors).

- **`ISignificanceTest`** receives a `SignificanceContext` (the comparable `Groups`, the `BaselineIndex`, the `Baseline` group, the non-baseline `Candidates`, and the `SignificanceLevel`) and returns a `SignificanceReport` with `Pairwise` (one `PairwiseComparison` per candidate), optional `Effect` metadata, optional `Shift` estimate (the built-in strategies populate the Hodges-Lehmann shift), and optional `Omnibus` verdict for omnibus tests. Use `PValue: null` for rules that don't produce a p-value. See [Significance Testing: Custom significance tests](../statistics/significance.md#custom-significance-tests).

- **The `MinimumPracticalEffect` gate works for any test.** The engine enforces the gate in `Significance.ApplyReport` after the test runs, so a custom test that returns an `EffectSize` with a `PracticalValue` is gated automatically. Tests that don't return a practical value are unaffected. See [Significance Testing: Practical-significance gate](../statistics/significance.md#practical-significance-gate).

- **Isolated children preserve your custom statistics.** Children rebuild the suite from your own `Main` rather than deserializing options, so custom detector / test instances are preserved across the process boundary. In Harness mode, scalar CLI overrides (iterations, warmup, confidence, etc.) are forwarded to each child. See [Isolated runs](../features/isolated-runs.md#important-behavior-notes).

> [!TIP] Compose the built-in strategies
> The built-in strategies - `MannWhitneyUSignificanceTest`, `KruskalWallisSignificanceTest`, and the group-count-aware `DefaultSignificanceTest` - all implement `ISignificanceTest`. You can wrap or compose them: run the built-in test, then add a domain-specific gate on top, or fall back to a custom rule when the built-in test returns `NotTested` (e.g. too few samples).

## Run it

```bash
# The custom detector's name shows up in the report header
dotnet run -c Release

# Outliers: keep fastest 90%
# Significance: median ratio (>25%)
```

```text
latency-slo
Benchmark | Median    | Mean     | Ops/s      | Ratio    | Sig | Mag   | Alloc/op
----------+-----------+----------+------------+----------+-----+-------+---------
v1        | 420.0 ns  | 422.3 ns | 2,380,952  | baseline |  -  |  -    |   128 B
v2        | 305.0 ns  | 307.1 ns | 3,278,689  | 0.73x    |  ✓  | large |    64 B
```

The custom detector and test are named in the header and footer so the report is self-describing - anyone reading it knows which statistics were applied.

## Read the results

The output is the same as any other run. The custom detector and test change which samples are kept and how significance is decided, but the columns, indicators, and warnings are unchanged. See [Reading Your Results](../output/reading-your-results.md).

One caveat: the `Magnitude` column reflects whatever your custom test returns in `EffectSize.Magnitude`. The built-in tests classify Cliff's delta into Negligible / Small / Medium / Large; a custom test can use any labels, but the console reporter color-codes based on the conventional labels. Stick to `neg` / `small` / `med` / `large` if you want the color coding to work.

## When to go deeper

- [Outlier Trimming: Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors) - the full `IOutlierDetector` contract, the `OutlierClassification` record, fence handling, and the `Name` property.
- [Significance Testing: Custom significance tests](../statistics/significance.md#custom-significance-tests) - the full `ISignificanceTest` contract, `SignificanceContext`, `PairwiseComparison`, `EffectSize`, `ShiftEstimate`, and `OmnibusComparison`.
- [Significance Testing: Practical-significance gate](../statistics/significance.md#practical-significance-gate) - how the `MinimumPracticalEffect` gate applies to any test that returns a `PracticalValue`.
- [Validation & Accuracy](../statistics/validation.md) - how the built-in statistical primitives are cross-validated against SciPy and NumPy, and what that means for a custom test that doesn't have the same validation.
- [Samples: ExtensibleStats](../samples.md#extensiblestats---custom-statistics) - a runnable sample project with the `KeepFastestDetector` and `MedianRatioSignificanceTest` shown above.


---

# performance-gates.md

---
title: Performance gates in your test suite
description: Fail PRs on performance regression inside your existing xUnit, NUnit, or MSTest test suite - no separate benchmark project or CI step required.
order: 6
---

# Performance gates in your test suite

## Scenario

You already run a unit test suite in CI. You don't want a separate benchmark project, a separate CI step, or a separate `--threshold-pct` invocation - you want a perf regression to fail a test the same way any other assertion failure fails a test, visible in the same test report and the same PR check.

NBenchmark's test-integration packages attach to xUnit, NUnit, and MSTest and run the benchmark as part of the test method. Thresholds can be absolute (a hard SLA: "this method must complete in under 500 µs") or relative (a regression gate: "this method must not be more than 5x slower than a reference"), and a relative threshold largely absorbs a change of machine, because both sides scale with machine speed.

> [!IMPORTANT]
> An earlier version of this page said relative thresholds were "hardware-independent because the comparison runs in the same test session". The second half of that is not true, and measurement says so: on four benchmark bodies of provably identical cost, running them in one test host produced a **2.80x** ratio between two of them, with a tight confidence interval on each side. Sharing a session does not cancel the host out - the host's JIT tiering state is *itself* the variable. That is why NBenchmark measures test bodies in worker processes, and why a ratio gate is only enforced between two measurements taken the same way. See [Where your test is measured](#where-your-test-is-measured).

## Complete example

### Absolute threshold (SLA-style)

```csharp
[PerformanceFact(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

If the measured mean exceeds 500,000 ns (500 µs), the test fails with a message describing the violation.

### Relative threshold (regression gate, zero config)

```csharp
[PerformanceFact(MaxSlowdownRatio = 5.0)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

Without a `ReferenceMethod`, the test runs a built-in CPU-bound calibration benchmark alongside your method. The ratio between your method and the calibration is stable across hardware for CPU-bound work - both scale with machine speed. The test fails only when the slowdown is **both** statistically significant (p < 0.05) **and** practically meaningful (ratio exceeds `MaxSlowdownRatio`). A significant-but-small slowdown passes (noise); a large-but-noisy slowdown passes (not enough evidence).

**The calibration is measured wherever your method is.** When the test is measured in an isolated worker, the worker measures the calibration too, in the same process and under the same runtime configuration. That matters more than it sounds: the worker starts with JIT tiering and ReadyToRun disabled and the test host does not, and on bodies of provably identical cost that difference alone was worth ~3.3x. A ratio spanning it would report the two process configurations rather than anything about your code. If the worker cannot produce a calibration, the gate falls back to the host's own and says so in the test output - treat that ratio as a rough hardware-scaled bound rather than a code comparison.

### Relative threshold with a reference method (compare two implementations)

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse))]
public void OptimisedParse() => OptimisedParser.Parse(Payload);

private static void NaiveParse() => NaiveParser.Parse(Payload);
```

The candidate must not exceed 1.2x the reference. The reference can be private and runs with the same measurement options as the candidate (same iterations, warmup, outlier mode), so the comparison is apples-to-apples - and, when both sides isolate, in matching worker processes.

### NUnit / MSTest equivalents

```csharp
// NUnit
[Performance(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// MSTest
[PerformanceTestMethod(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

### Assert pattern (measure one part of a larger test)

```csharp
[Test]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 });
}
```

`PerformanceAssert.Run` is available in NUnit and MSTest and supports calibration mode only (no `ReferenceMethod`).

## What's happening

- **Attribute pattern** - replace the test attribute on a test method. The entire method body becomes the benchmark. Thresholds are set as named arguments on the attribute. Available in all three frameworks.

- **Assert pattern** - call `PerformanceAssert.Run` from inside any test. The benchmark runs inline and violations fail the test immediately. Useful when you want to measure just one part of a larger test. Available in NUnit and MSTest.

- **Absolute thresholds** (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`) - hard SLAs. Susceptible to shared-runner noise; prefer `MaxSlowdownRatio` for regression gates. Set `MaxAbsoluteThresholdTolerance` to relax absolute thresholds when a shared runner or high-jitter host is detected (e.g. `1.25` for 25% relaxation).

- **Relative thresholds** (`MaxSlowdownRatio`) - regression gates. Comparing two bodies measured in the same session cancels out how *fast the machine is*, so a quick dev box and a slow CI runner agree on the ratio, with no stored baselines or environment matching. The test fails only when the slowdown is both statistically significant and exceeds the ratio.

  **A ratio cancels the hardware, not the runtime state.** This guide previously said the host "cancels out" outright; that is measurably false. On four benchmarks of *provably identical cost*, repeated in-process runs fabricated a **2.80x** ratio between two of them - each reported with a tight confidence interval. Whatever the JIT happened to have tiered up when a given body ran does not cancel, because it is not shared between the two bodies. Set `MaxSlowdownRatio` loosely enough to survive that (start around `10.0` and tighten from observed CI runs), and lean on the statistical gate rather than the ratio alone.

- **Statistical gating** - the test fails only when the slowdown is **both** statistically significant (Mann-Whitney U p-value below the significance level) **and** practically meaningful (ratio exceeds `MaxSlowdownRatio`). A significant-but-small slowdown passes; a large-but-noisy slowdown passes. This mirrors the [practical-significance gate](../statistics/significance.md#practical-significance-gate) in the suite / harness flow.

## Where your test is measured

Performance tests are measured in a **worker process**, not in the test host - the same isolation the rest of NBenchmark uses, for the same reason: JIT tiering, dynamic PGO and GC flavour are fixed when a process starts, and a test host's are whatever the preceding tests left behind.

The worker builds your test class itself, so this works when the class can be constructed from nothing. That covers the ordinary case. It cannot cover a class the test framework injects into, and NBenchmark says so rather than guessing:

| Situation | Where it runs | Reported as |
| --- | --- | --- |
| Plain test class, simple or no arguments | Worker | `Isolated` |
| Static test class or method | Worker | `Isolated` |
| `IClassFixture`, `ITestOutputHelper`, constructor injection | Test host | `InProcessLiveFixture` |
| An argument that is an object graph or mock | Test host | `InProcessLiveFixture` |
| No worker deployed | Test host | `InProcessNoWorker` |

The reason is printed with the test's metrics, naming the specific parameter or dependency:

```
NBenchmark: 'ParserTests.Parse' measured in the test host - parameter 'documents'
(of type 'List`1') is a live object that exists only in this test process.
```

Those results are still produced and still gated on absolute thresholds. What changes is the **ratio** gate.

### When a ratio gate is enforced

| Candidate | Reference | Ratio gate |
| --- | --- | --- |
| Worker | Worker | **Enforced.** |
| Test host | Test host | Not enforced, and the reason is logged. Add `[AllowInProcessGate]` to enforce it anyway. |
| Worker | Test host (or the reverse) | **Never enforced.** No opt-in covers it. |

The middle row is the one to understand. Two bodies measured in the same test host share its JIT tiering and PGO state, and that state is whatever the preceding tests left behind - the source of the 2.80x fabricated ratio above. The gate declines rather than reporting an effect that may not exist, and prints why:

```
NBenchmark: the ratio gate for 'ParserTests.Parse' was not enforced - both it and its
reference were measured in the test host, where the runtime configuration is whatever
the preceding tests left behind. On bodies of provably identical cost that produced a
2.80x ratio with a tight interval. Make the test isolatable, or add
[AllowInProcessGate] to gate on it anyway.
```

The bottom row is refused outright: a ratio spanning a process boundary is dominated by the difference between the two runtime configurations, which on the same identical-cost bodies was worth about 3.3x. Making both sides isolatable - usually by moving injected state into the method - is the fix.

### `[AllowInProcessGate]`

Applies to a method, a class, or a whole assembly. It says: this test cannot be isolated, and a noisy ratio is more useful to me than none.

```csharp
[AllowInProcessGate]
public class ParserTests : IClassFixture<ParserFixture>
{
    [PerformanceFact(MaxSlowdownRatio = 1.5, ReferenceMethod = nameof(Naive))]
    public void Optimised() => _fixture.Parser.Parse(Payload);
}
```

The gate then runs on host measurements and the result carries a note saying so. Treat a marginal outcome as inconclusive rather than as evidence.

### `RequireIsolation`

The opposite lever: fail the test when the measurement was *not* taken in a worker.

```csharp
[PerformanceFact(MaxMeanNs = 500_000, RequireIsolation = true)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

Use it on gates that matter. Isolation can be lost quietly - somebody adds a fixture argument, or the worker fails to deploy on a build agent - and the test keeps passing against a number measured somewhere you did not choose. `RequireIsolation` turns that into a failure that names the reason and its remedy. It applies to absolute-threshold gates too, and is available on all three attributes and on the `PerformanceAssert` option bags.

Simple values reach the worker intact: `int`, `string`, `bool`, `enum`, `decimal`, `DateTime`, `Guid` and the like, so `[InlineData]` and `[DataRow]` cases isolate normally. Object arguments are refused rather than reconstructed, because a reconstruction that is usually right is worse than one that declines.

> [!TIP] Absolute vs. relative - which to use?
> Use **absolute** thresholds only when you have a hard SLA ("parse must complete in under 500 µs"). Use **relative** thresholds for regression gates ("this PR must not regress the parser"). Relative thresholds tolerate a change of machine, which absolute ones do not. Start `MaxSlowdownRatio` loose (e.g. `10.0`) and tighten from several runs in your own CI environment.

## Run it

The test runs as part of your normal test suite - no special invocation needed:

```bash
dotnet test
dotnet test --filter "FullyQualifiedName~ParseJson"
```

To pin the measurement (e.g. for reproducibility in CI), set `Iterations` and `WarmupIterations` on the attribute:

```csharp
[PerformanceFact(MaxSlowdownRatio = 5.0, Iterations = 200, WarmupIterations = 20)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

## Read the results

A passing test reports normally. A failing test prints the violation:

```text
PerformanceAssert: mean 612,345 ns exceeded MaxMeanNs 500,000 ns
  median: 598,210 ns  mean: 612,345 ns  p95: 720,000 ns
  alloc/op: 1,024 B
```

For a relative-threshold failure:

```text
PerformanceAssert: slowdown ratio 6.2x exceeded MaxSlowdownRatio 5.0
  candidate median: 612,345 ns  reference median: 98,762 ns
  p = 0.0003 (significant)  Cliff's delta = 0.92 (large)
```

The `p` and Cliff's delta values tell you whether the slowdown is real and how large. See [Reading Your Results](../output/reading-your-results.md) for every column the underlying benchmark reports.

## How this differs from `--threshold-pct`

| | Test-integration packages | Harness `--threshold-pct` |
| --- | --- | --- |
| Lives in | Your existing test suite | A dedicated benchmark project |
| Trigger | `dotnet test` | `dotnet run -- --threshold-pct 10` |
| Comparison | Your method vs. a calibration / `ReferenceMethod` (same session) | Each benchmark vs. the suite baseline (same session) |
| Survives a change of machine | Yes (relative thresholds) | No (absolute medians) |
| Exit code | Test failure | `Environment.ExitCode = 1` |
| Best for | "Don't regress this hot path" | "Don't regress any benchmark in the suite" |

Both are valid; they serve different scopes. The test-integration packages are per-method and live where your tests live; `--threshold-pct` is per-suite and lives where your benchmarks live. See [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) for the `--threshold-pct` path.

## When to go deeper

- [Test integration](../test-integration/index.md) - the full threshold reference, the attribute vs. assert patterns, calibration vs. `ReferenceMethod`, and `MaxAbsoluteThresholdTolerance` for shared runners.
- [xUnit integration](../test-integration/xunit.md) / [NUnit integration](../test-integration/nunit.md) / [MSTest integration](../test-integration/mstest.md) - per-framework setup and examples.
- [Significance Testing](../statistics/significance.md) - how the Mann-Whitney U test and Cliff's delta underpin the statistical gating.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - the `--threshold-pct` alternative and the noise-reduction stack that applies to both.
- [Configuration](../reference/configuration.md) - the underlying `MeasurementOptions` that the integration attributes expose.


---

# index.md

---
title: Guides
description: Real-world workflow recipes that combine NBenchmark features to solve common benchmarking tasks end-to-end.
order: 4
---

# Guides

The [Features](../features/) section documents each capability on its own. Real benchmarks rarely use one feature at a time - a parameterized EF Core benchmark needs Harness mode, dependency injection, and parameter cases together; a CI regression gate needs isolation, environment control, and the threshold check together.

These guides are **workflow-first**. Each one starts from a concrete goal you might actually have - "I refactored a hot path, is it really faster?" or "I need to fail the build when a benchmark regresses" - and assembles the relevant features into a complete, copy-pasteable example. Inline snippets omit `using` statements for brevity; each guide links out to the feature pages for depth.

## Recipes

### [Benchmarking ASP.NET Core services](./aspnet-core-services.md)

Benchmark an EF Core query or ASP.NET service end-to-end: Harness mode, scoped dependency injection, parameterized cases, and categories. Includes the shared-state pitfall (`PerClass` lifetime with a scoped `DbContext`) and how the NB0011 analyzer catches it.

### [Tuning for CI/CD pipelines](./ci-cd-pipelines.md)

Get clean numbers on a noisy shared runner and fail the build on regression. Combines isolated runs, environment control (CPU affinity, process priority, dedicated-host guidance), the `--threshold-pct` gate, and `--launch-count` as the honest signal on a contested host. Includes a minimal GitHub Actions snippet.

### [Comparing a refactor side-by-side](./refactor-comparison.md)

"I changed a hot path - is it really faster?" Suite mode with a baseline, the Sig and Magnitude columns, the practical-significance gate, and cross-class significance when the old and new implementations live in separate classes.

### [Parameter sweeps across input sizes](./parameter-sweeps.md)

See how an algorithm scales across input sizes. Parameterized Suite mode (`WithParameter`) and parameterized Harness mode (`[BenchmarkCase]` / `[BenchmarkCases]`), plus how to read the scaling trend in the output table.

### [Cross-runtime comparison](./cross-runtime.md)

Verify your code benefits from net10 vs net8. Multi-runtime in Suite mode (`WithRuntimes`) and Harness mode (`--runtimes` / `[Runtimes]`), the `<TargetFrameworks>` project setup, always-child-process isolation, and significance grouped within each runtime.

### [Performance gates in your test suite](./performance-gates.md)

Fail PRs on performance regression inside your existing xUnit, NUnit, or MSTest test suite - no separate benchmark project or CI step required. Covers `[PerformanceFact]` / `[Performance]` / `[PerformanceTestMethod]`, `PerformanceAssert.Run`, absolute vs. relative thresholds, and how this differs from the Harness `--threshold-pct` gate.

### [Custom statistics](./custom-statistics.md)

The built-in IQR/MAD outlier trimming or rank-based significance tests don't fit your domain (latency SLOs, fixed physical thresholds, bootstrap comparison). Swap in a custom `IOutlierDetector` and a custom `ISignificanceTest`.

## How to use these guides

Each guide is self-contained and produces a runnable example. The pattern is:

1. **Scenario** - the goal you arrived with.
2. **Complete example** - the configuration body, copy-pasteable.
3. **What's happening** - brief callouts on the feature interactions, linking to the feature pages for depth.
4. **Run it** - the `dotnet run` / CLI invocations.
5. **Read the results** - plain-English, linking to [Reading Your Results](../output/reading-your-results.md).
6. **When to go deeper** - links into the relevant feature and statistics pages.

If you're new to NBenchmark, start with the [Quick Start](../getting-started/quick-start.md) and [Key Concepts](../getting-started/key-concepts.md), then come back here once you have a specific goal in mind.

## See also

- [Features](../features/) - per-feature reference for every capability used in these guides.
- [Usage modes](../usage-modes/) - the four ways to run benchmarks (Single, Suite, Harness, Global Tool).
- [Configuration](../reference/configuration.md) - the full `MeasurementOptions` reference.
- [Statistics](../statistics/) - the mathematical methodology behind the numbers.


---

# parameter-sweeps.md

---
title: Parameter sweeps across input sizes
description: See how an algorithm scales across input sizes with parameterized Suite mode and Harness mode, and read the scaling trend in the output.
order: 4
---

# Parameter sweeps across input sizes

## Scenario

You have an algorithm - a sort, a parse, a query, a hash - and you want to see how it scales across input sizes. A single number at `n=1000` doesn't tell you whether it's O(n log n) or O(n²); a sweep across `n = 10, 100, 1000, 10000` does. Parameterized benchmarks run the same method body across multiple input values and produce one benchmark entry per parameter combination, so the scaling trend is visible in a single table.

## Complete example

### Suite mode - `WithParameter`

```csharp
var results = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1_000, 10_000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

This produces four benchmarks: `sort(size=10)`, `sort(size=100)`, `sort(size=1_000)`, `sort(size=10_000)`.

### Harness mode - `[BenchmarkCase]` and `[BenchmarkCases]`

For a small literal list:

```csharp
public class SortingBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [BenchmarkCase(1_000)]
    [BenchmarkCase(10_000)]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }
}
```

For a generated sweep (powers of ten, file-backed inputs, or any source method):

```csharp
public class SortingBenchmarks
{
    [BenchmarkCases(nameof(SortCases))]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }

    public static IEnumerable<(int N)> SortCases()
    {
        for (var p = 1; p <= 7; p++)
            yield return ((int)Math.Pow(10, p));
    }
}
```

### Comparing algorithms across the same sweep

The real power is sweeping two or more algorithms across the same inputs and reading the scaling trend against the baseline:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100, 1_000, 10_000)
    .Add("linear", (int size) => LinearSearch(size))
    .Add("binary", (int size) => BinarySearch(size))
    .WithBaseline("linear")
    .WithRunOrder(RunOrder.Declaration)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## What's happening

- **`WithParameter("size", ...)`** (Suite) / **`[BenchmarkCase(...)]`** / **`[BenchmarkCases(nameof(Source))]`** (Harness) - the three ways to declare a sweep. Each value (or Cartesian product across multiple parameters) becomes a separate benchmark entry with a distinct display name. See [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) and [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

- **Significance is grouped by parameter set.** The `size=100` benchmarks are compared against each other, not against `size=10_000`. A parameterized suite with `N` parameter combinations and `M` benchmark methods produces `N` separate significance comparisons, each over `M` benchmarks - rather than one flat comparison over `N * M` results. This keeps each comparison apples-to-apples.

- **Run order.** `RunOrder.Declaration` runs the sweep in size order, which makes the scaling trend easy to read in the output. `RunOrder.Random` (the default) shuffles within each parameter group, which guards against systematic bias but scrambles the trend. For a sweep you're reading visually, declaration order is usually clearer.

- **Baselines expand across parameters.** `WithBaseline("linear")` marks every `linear(size=...)` variant as a baseline. Each parameter group gets its own `linear(size=N)` baseline, and the `binary(size=N)` row is compared against it.

> [!NOTE] Single-method sweeps rank against the fastest point
> When a single method is swept across parameter values (no second algorithm to compare), every parameter group holds just one benchmark, so there's no within-group comparison. The table instead ranks every row against the fastest point: the `Ratio` column reports each point's scaling factor (the fastest point is the `baseline`), and `Sig` / `Mag` stay `-`, because the engine does not test different workloads against one another. This makes scaling trends easy to read.

## Run it

```bash
# Suite mode - the full sweep
dotnet run -c Release

# Harness mode - filter to a subset of sizes by display name
dotnet run -c Release -- --filter "*n=1000*" --filter "*n=10000*"

# Pin the run so the sweep is reproducible across CI and local
dotnet run -c Release -- --iterations 200 --warmup 20 --order declaration
```

## Read the results

A single-method sweep produces a scaling table where the `Ratio` column is the most useful signal:

```text
SortingBenchmarks
Benchmark    | n     | Median    | Mean      | Ops/s      | Ratio               | Sig | Mag | Alloc/op
-------------+-------+-----------+-----------+------------+---------------------+-----+-----+---------
Sort         |    10 |   31.2 ns |   32.7 ns | 30,567,164 | baseline            |  -  |  -  |    24 B
Sort         |   100 |  117.2 ns |  132.2 ns |  7,565,906 | 3.76x               |  -  |  -  |    24 B
Sort         |  1000 |    1.40 µs|   1.27 µs |    789,515 | 44.87x              |  -  |  -  | 4,048 B
Sort         | 10000 |   18.7 µs |  18.9 µs  |     53,476 | 599.0x              |  -  |  -  | 48,024 B
```

Reading the trend:

- **Median × 10 per 10× input** - the median scales roughly linearly (10 → 100 is 3.76x, 100 → 1000 is 11.9x, 1000 → 10000 is 13.4x). Closer to O(n log n) than O(n²) (which would be 10x per step) but with a super-linear constant.
- **Alloc/op growing linearly with `n`** - the allocation tracks the input size, which is expected for `Enumerable.Range(...).ToArray()`.
- **Ratio against the fastest point** - the `baseline` is `n=10`; every other row shows how many times slower it is than that.

A two-algorithm sweep produces a comparison table grouped by parameter set, where each group has its own baseline and its own significance verdict:

```text
search
Benchmark | size | Median   | Mean     | Ops/s      | Ratio             | Sig | Mag    | Alloc/op
----------+------+----------+----------+------------+-------------------+-----+--------+---------
linear    |   10 |  90.0 ns |  91.2 ns | 11,111,111 | baseline          |  -  |  -      |    32 B
binary    |   10 |  85.0 ns |  86.1 ns | 11,764,706 | 0.94x             |  ✗  | neg    |    32 B
linear    |  100 | 250.0 ns | 252.1 ns |  4,000,000 | baseline          |  -  |  -      |    32 B
binary    |  100 | 110.0 ns | 112.4 ns |  9,090,909 | 0.44x             |  ✓  | large  |    32 B
```

Here `binary(size=100)` is 2.27x faster than `linear(size=100)` with a `large` effect and `✓` significance, while `binary(size=10)` is barely different and not significant. The scaling trend tells you where the algorithm choice starts to matter.

See [Reading Your Results](../output/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) - `WithParameter` for up to 3 parameters, mixed parameterized and plain benchmarks, supported parameter types, baselines, and significance grouping.
- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - `[BenchmarkCase]` vs. `[BenchmarkCases]`, named-tuple display names, `--filter` by display name, the Suite vs. Harness comparison table.
- [Suite mode: Run order](../usage-modes/suite-mode.md#run-order) - why declaration order is usually clearer for sweeps, and why randomization is the default for comparisons.
- [Configuration: Iterations](../reference/configuration.md#iterations) - pinning the run for reproducible sweeps across CI and local.
- [Reading Your Results](../output/reading-your-results.md) - the full column reference, including how the `Ratio` column behaves for single-method vs. multi-method sweeps.


---

# aspnet-core-services.md

---
title: Benchmarking ASP.NET Core services
description: Benchmark an EF Core query or ASP.NET service end-to-end with Harness mode, scoped dependency injection, parameterized cases, and categories.
order: 1
---

# Benchmarking ASP.NET Core services

## Scenario

You have a service or repository method that hits a database (EF Core, Dapper, or a raw connection) and you want to measure it under realistic conditions: real query plans, real serialization, parameterized inputs, and real DI lifetimes. The benchmark needs a scoped `DbContext` per method, multiple input sizes, and a way to group related benchmarks together.

## Complete example

This is a complete `Program.cs` for a dedicated benchmark project that targets a real ASP.NET Core service. It uses Harness mode for attribute-based discovery, scoped DI so each `[Benchmark]` method gets a fresh `DbContext`, parameterized cases to sweep input sizes, and categories to keep the suite navigable.

```csharp
var services = new ServiceCollection()
    .AddDbContext<BenchDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHarness.Create(args)
    .UseScopedDependencyInjection<OrderBenchmarks>(services)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public sealed class OrderBenchmarks(BenchDbContext db)
{
    [Benchmark]
    [BenchmarkCategory("Read")]
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [BenchmarkCase(1_000)]
    public int ListRecentOrders(int limit)
        => db.Orders.OrderByDescending(o => o.Id).Take(limit).Count();

    [Benchmark]
    [BenchmarkCategory("Write")]
    public int InsertOrder()
    {
        db.Orders.Add(new Order());
        return db.SaveChanges();
    }
}
```

## What's happening

- **`UseScopedDependencyInjection<T>(sp)`** does three things in one call: discovers `T`'s assembly, configures the host to resolve benchmark instances from the supplied service provider, and creates a fresh DI scope per `[Benchmark]` method. The scope is disposed in per-method teardown, so any `IDisposable` / `IAsyncDisposable` services (`DbContext`, `HttpClient`, etc.) are cleaned up. See [Dependency Injection](../features/dependency-injection.md).

- **`AddDbContext` + `UseScopedDependencyInjection`** gives each benchmark method a fresh `DbContext`. With `PerMethod` lifetime (the default), method A cannot warm the entity cache that method B reads. See the [lifetime and disposal table](../features/dependency-injection.md#lifetime-and-disposal-semantics) for the full matrix.

- **`[BenchmarkCase(...)]`** expands the method into one benchmark per case. The display name carries the parameter: `ListRecentOrders(limit=10)`, `ListRecentOrders(limit=100)`, `ListRecentOrders(limit=1_000)`. Significance is grouped by parameter set, so the `limit=100` benchmarks are compared against each other, not against `limit=1_000`. See [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

- **`[BenchmarkCategory(...)]`** tags benchmarks for filtering. Run only the read path with `dotnet run -- --category Read`, or exclude writes with `dotnet run -- --exclude-category Write`. See [Categories](../features/categories.md).

> [!WARNING] Shared state breaks statistical independence
> If you pair `UseScopedDependencyInjection` with `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in the class share one instance and one `DbContext`. The cache warms across methods, method B's timings become linked to method A running first, and the significance test's independence assumption is violated. The **NB0011 analyzer** warns on this combination at build time. See [State isolation](../features/state-isolation.md) for the `IStateReset` contract and the auto-isolation fallback that enforce independence at runtime.

## Run it

From the benchmark project directory:

```bash
# Everything
dotnet run -c Release

# Just the read benchmarks, at the two larger sizes
dotnet run -c Release -- --category Read --filter "*limit=100*" --filter "*limit=1_000*"

# Smoke test: run the body once, no warmup, no measurement
dotnet run -c Release -- --dry-run
dotnet run -c Release -- --iterations 1 --warmup 0

# Publish JSON for the CI dashboard
dotnet run -c Release -- --reporter json --output ./results
```

The harness is **isolated by default**: each benchmark class runs in its own freshly spawned child process, so JIT, GC, and thread-pool state from one class cannot bias another. See [Isolated runs](../features/isolated-runs.md).

## Read the results

The console reporter prints one comparison table per class, grouped by parameter set. The columns you care about:

- **Median** - the middle timing. Compare this across the parameter values to see scaling.
- **Ratio** - speed relative to the baseline. `0.75x` = 25% faster; `2.0x` = twice as slow.
- **Sig** - **✓** means the difference from the baseline is statistically real (p < 0.05); **✗** means the measurements are too noisy to tell.
- **Magnitude** - how large the difference is (Negligible / Small / Medium / Large). A ✓ with a Negligible magnitude is real but too small to act on.
- **Alloc/op** - mean heap allocation per operation. EF Core query materialization is allocation-heavy; this column is often the most actionable signal.

See [Reading Your Results](../output/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Harness mode: BenchmarkHarness](../usage-modes/harness-mode.md) - the full attribute reference (`[BenchmarkSetup]`, `[BenchmarkIterationSetup]`, `[IsolatedProcess]`, `[Runtimes]`, etc.).
- [Dependency Injection](../features/dependency-injection.md) - scoped vs. root provider, multiple assemblies, non-Microsoft containers, the `WithInstanceFactory` escape hatch.
- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - `[BenchmarkCases]` for generated or file-backed inputs, named-tuple display names.
- [State isolation](../features/state-isolation.md) - `IStateReset` for `PerClass` classes that share state intentionally.
- [Analyzers](../reference/analyzers.md) - the NB0001-NB0013 Roslyn diagnostics, including NB0011 (PerClass + scoped service).
- [Performance gates in your test suite](./performance-gates.md) - if you want this comparison to fail a PR on regression instead of just printing a table.


---

# refactor-comparison.md

---
title: Comparing a refactor side-by-side
description: I changed a hot path - is it really faster? Suite mode with a baseline, the Sig and Magnitude columns, the practical-significance gate, and cross-class significance.
order: 3
---

# Comparing a refactor side-by-side

## Scenario

You refactored a hot method. The new version looks faster locally, but you've been burned by 2% "improvements" that turned out to be noise. You want a side-by-side comparison that answers two questions: (1) is the difference statistically real, and (2) is it large enough to matter?

NBenchmark answers both in the same output: **Sig** tells you whether the difference is real, **Magnitude** tells you whether it's large enough to act on, and **Ratio** tells you the direction and rough size. The `MinimumPracticalEffect` gate (on by default) makes a ✓ always mean "real **and** at least a small effect", so a sub-small but statistically real difference is downgraded rather than celebrated.

## Complete example

### Both implementations in one suite

If the old and new implementations are callable from the same project, a single `BenchmarkSuite` is the simplest path:

```csharp
var results = await new BenchmarkSuite("parser-refactor")
    .Add("v1-current", () => CurrentParser.Parse(Payload))
    .Add("v2-candidate", () => CandidateParser.Parse(Payload))
    .WithBaseline("v1-current")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

### Old and new in separate classes (cross-class significance)

When the legacy and refactored code live in separate classes (common when one is in a legacy project and the other in a new one), use Harness mode with `--cross-class`:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<LegacyParserBenchmarks>()
    .AddFromAssembly<CandidateParserBenchmarks>()
    .WithCrossClassSignificance()
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public sealed class LegacyParserBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Current() => LegacyParser.Parse(Payload);
}

public sealed class CandidateParserBenchmarks
{
    [Benchmark]
    public int Candidate() => CandidateParser.Parse(Payload);
}
```

The console reporter adds a `Class` column so the rows are distinguishable, and the baseline is chosen from the whole group.

## What's happening

- **`WithBaseline("name")`** designates the reference point. The **Ratio** column shows how fast each other benchmark is relative to the baseline (`0.75x` = 25% faster; `2.0x` = twice as slow), and significance is tested against it. If no baseline is set, the lowest-median benchmark is the implicit baseline. See [Suite mode](../usage-modes/suite-mode.md#setting-a-baseline).

- **Sig** - **✓** means the difference would occur by chance less than 5% of the time (p < 0.05, two-sided). **✗** means the measurements are too noisy to conclude one is faster. (blank) means the benchmark is the baseline or significance wasn't tested. The default test is non-parametric and rank-based; for two benchmarks it's a Mann-Whitney U test, for three or more an omnibus test gates pairwise comparisons. See [Significance Testing](../statistics/significance.md).

- **Magnitude** - classifies the effect size as Negligible / Small / Medium / Large from Cliff's delta. A statistically significant result (✓) with a Negligible magnitude means the difference is real but too small to care about. Positive = candidate slower (red in the console); negative = candidate faster (green). See [Reading Your Results: Magnitude](../output/reading-your-results.md#magnitude-suite-mode).

- **`MinimumPracticalEffect`** (default `0.147`, the Romano negligible/small boundary). A ✓ means "real **and** at least a small effect". A sub-threshold difference is downgraded to `NotSignificant`, its magnitude is forced to `neg`, and a warning records the downgrade so it's discoverable. Set `--min-practical-effect 0` to restore p-value-only verdicts, or `null` to disable the gate. See [Significance Testing: Practical-significance gate](../statistics/significance.md#practical-significance-gate).

- **Cross-class significance** (`--cross-class` / `WithCrossClassSignificance()`). By default Harness mode computes significance **per class** - each class gets its own baseline. Cross-class mode is opt-in because mixing unrelated benchmark classes into one significance table produces a baseline that may be semantically meaningless. Use it when the classes are genuinely competing implementations of the same thing. See [Harness mode: Cross-class significance](../usage-modes/harness-mode.md#cross-class-significance).

> [!TIP] Read all three columns together
> A ✓ with a small Ratio (e.g. `1.01x`) and a Negligible Magnitude means the difference is real but tiny - probably not worth the refactor. A ✗ with a large Ratio (e.g. `1.5x`) means the measurements are too noisy to tell - reduce noise (see [Tuning for CI/CD pipelines](./ci-cd-pipelines.md)) or collect more samples. The interesting result is a ✓ with a Small / Medium / Large Magnitude and a meaningful Ratio.

## Run it

```bash
# Suite mode - one comparison table
dotnet run -c Release -- --filter "parser-refactor*"

# Harness mode, cross-class - one table across both classes
dotnet run -c Release -- --cross-class

# Demand a tighter confidence interval for a publication-grade result
dotnet run -c Release -- --cross-class --auto-tune thorough

# Pin the run for reproducibility across CI and local
dotnet run -c Release -- --cross-class --iterations 500 --warmup 50 --order declaration
```

## Read the results

A typical verdict for a real refactor looks like:

```text
parser-refactor
Benchmark     | Median    | Mean      | Ops/s      | Ratio      | Sig | Mag    | Alloc/op
--------------+-----------+-----------+------------+------------+-----+--------+---------
v1-current    | 420.0 ns  | 422.3 ns  | 2,380,952  | baseline   |  -  |  -     |   128 B
v2-candidate  | 305.0 ns  | 307.1 ns  | 3,278,689  | 0.73x      |  ✓  | large  |    64 B
```

- **Ratio `0.73x`** - the candidate is ~27% faster.
- **Sig `✓`** - the difference is statistically real (not noise).
- **Mag `large`** - the effect size is large (the two distributions barely overlap).
- **Alloc/op `64 B` vs `128 B`** - the candidate also halves the allocation, which often explains the speedup.

If the row had read `Sig ✓ | Mag neg`, the difference would be real but below the practical-significance threshold - the refactor is statistically faster but not enough to act on. If it had read `Sig ✗ | Mag large`, the measurements would be too noisy; reduce noise or collect more samples before deciding.

See [Reading Your Results](../output/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API, including `WithSignificance(false)` to disable significance and `WithSignificanceLevel(alpha)` to demand stronger evidence.
- [Harness mode: Cross-class significance](../usage-modes/harness-mode.md#cross-class-significance) - the `Class` column, baseline selection, when to use cross-class vs. per-class.
- [Significance Testing](../statistics/significance.md) - the Mann-Whitney U and Kruskal-Wallis algorithms, the Holm-Bonferroni correction, Cliff's delta thresholds, and the `MinimumPracticalEffect` gate in detail.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - what to do when `Sig` is `✗` and the Ratio is large (the noise problem).
- [Configuration: Significance](../reference/configuration.md) - the `EnableSignificance`, `SignificanceLevel`, `MinimumPracticalEffect`, and `SignificanceTest` options.


---

