---
title: Performance gates in your test suite
description: Fail PRs on performance regression inside your existing xUnit, NUnit, or MSTest test suite - no separate benchmark project or CI step required.
order: 6
---

# Performance gates in your test suite

## Scenario

You already run a unit test suite in CI. You don't want a separate benchmark project, a separate CI step, or a separate `--threshold-pct` invocation - you want a perf regression to fail a test the same way any other assertion failure fails a test, visible in the same test report and the same PR check.

NBenchmark's test-integration packages attach to xUnit, NUnit, and MSTest and run the benchmark as part of the test method. Thresholds can be absolute (a hard SLA: "this method must complete in under 500 µs") or relative (a regression gate: "this method must not be more than 5x slower than a reference"), and a relative threshold largely absorbs a change of machine, because both sides scale with machine speed.

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

**The calibration is measured wherever your method is.** When the test is measured in an isolated worker, the worker measures the calibration too, in the same process and under the same runtime configuration. That matters: the worker starts with JIT tiering and ReadyToRun disabled and the test host does not, and that configuration gap can move the reported number by ~3x on its own. A ratio spanning it would report the two process configurations rather than anything about your code. If the worker cannot produce a calibration, the gate falls back to the host's own and says so in the test output - treat that ratio as a rough hardware-scaled bound rather than a code comparison.

### Relative threshold with a reference method (compare two implementations)

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse))]
public void OptimisedParse() => OptimisedParser.Parse(Payload);

private static void NaiveParse() => NaiveParser.Parse(Payload);
```

The candidate must not exceed 1.2x the reference. The reference can be private and runs with the same measurement options as the candidate (same iterations, warmup, outlier mode), so the comparison is apples-to-apples - and, when both sides isolate, in the *same* worker process, so the ratio has that worker's core draw and memory layout divided out rather than left in it.

What that ratio does not have, at the default `LaunchCount = 1`, is an interval: it is one quotient, and nothing about it says whether a re-run would agree. Add replicates when the gate decides a build:

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse), LaunchCount = 3)]
public void OptimisedParse() => OptimisedParser.Parse(Payload);
```

Three workers, each measuring the pair, each producing its own ratio. The gate then applies the threshold to the combined estimate and fails only when the interval excludes `1.00x` - so a failure means the slowdown is larger than the difference between two runs of the same code. See [replicates and the paired ratio](../test-integration/index.md#replicates-and-the-paired-ratio).

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

- **Relative thresholds** (`MaxSlowdownRatio`) - regression gates. Comparing two bodies measured in the same session cancels out how *fast the machine is*, so a quick dev box and a slow CI runner agree on the ratio, with no stored baselines or environment matching. The ratio cancels the machine, not the runtime state the host happens to be in - set it loosely enough to survive that (start around `10.0` and tighten from observed CI runs), and lean on the statistical gate rather than the ratio alone. The test fails only when the slowdown is both statistically significant and exceeds the ratio.

- **Statistical gating** - the test fails only when the slowdown is **both** real **and** practically meaningful (ratio exceeds `MaxSlowdownRatio`). A significant-but-small slowdown passes; a large-but-noisy slowdown passes. This mirrors the [practical-significance gate](../statistics/significance.md#practical-significance-gate) in the suite / harness flow.

  What counts as *real* depends on `LaunchCount`. At the default of one launch it is a Mann-Whitney U p-value below the significance level, computed on the samples of that single measurement. At two or more it is the paired ratio interval excluding `1.00x`, which is a statement about reproducibility rather than about sample count - and the stronger claim, because a pooled sample count grants statistical power regardless of whether the difference survives a re-run.

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
| Worker | Worker | **Enforced.** With `LaunchCount >= 2`, only when the paired interval excludes `1.00x`; the reason is logged when it does not. |
| Test host | Test host | Not enforced, and the reason is logged. Add `[AllowInProcessGate]` to enforce it anyway. |
| Worker | Test host (or the reverse) | **Never enforced.** No opt-in covers it. |

The middle row is the one to understand. Two bodies measured in the same test host share its JIT tiering and PGO state, and that state is whatever the preceding tests left behind - so a ratio between them can report the host's history rather than the code. The gate declines rather than reporting an effect that may not exist, and prints why:

```
NBenchmark: the ratio gate for 'ParserTests.Parse' was not enforced - both it and its
reference were measured in the test host, where the runtime configuration is whatever
the preceding tests left behind. Make the test isolatable, or add
[AllowInProcessGate] to gate on it anyway.
```

The bottom row is refused outright: a ratio spanning a process boundary is dominated by the difference between the two runtime configurations, which can be ~3x on bodies of identical cost. Making both sides isolatable - usually by moving injected state into the method - is the fix.

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

### Isolation is required by default

The same attribute is also the *only* opt-out from the isolation requirement. A performance gate fails when its measurement was not taken in a worker process:

```csharp
// Fails if this ends up measured in the test host - no configuration needed.
[PerformanceFact(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

That default is deliberate. Isolation can be lost quietly - somebody adds a fixture argument, or the worker fails to deploy on a build agent - and a labelled-but-passing test is indistinguishable from a healthy one, because CI does not read output. Failing is the conservative direction: the message names the reason and its remedy.

Add `[AllowInProcessGate]` to accept a host measurement, at method, class or assembly scope. It waives both the isolation requirement and the ratio-gate restriction above, because both are the same judgement - "this test cannot be isolated and I accept a noisier number". The result then carries a note saying where it was measured.

There is deliberately no `RequireIsolation = false` on the attributes. xUnit reads attribute values as named arguments, where an absent argument and an explicit `false` are indistinguishable, and attribute arguments cannot be nullable to tell them apart - so the setting would have been silently ignored on one framework and honoured on the others. The `PerformanceAssert` option bags do expose `RequireIsolation`, because they are ordinary objects with no attribute target to carry `[AllowInProcessGate]`.

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
