---
title: Comparing a refactor side-by-side
description: I changed a hot path - is it really faster? Suite mode with a baseline, the Sig and Magnitude columns, the practical-significance gate, and cross-class significance.
order: 3
---

# Comparing a refactor side-by-side

## Scenario

If you refactored a hot method, the new version might look faster locally, but small "improvements" can often be noise. You need a side-by-side comparison that answers two questions:
1. Is the difference statistically real?
2. Is the difference large enough to be meaningful?

NBenchmark answers both in a single output: **Sig** indicates if the difference is real, **Magnitude** indicates if it is large enough to act on, and **Ratio** provides the direction and rough size of the change. By default, the engine uses a `MinimumPracticalEffect` gate, meaning a **✓** indicates that the difference is both statistically real and at least a "small" effect. Any difference that is statistically real but below this threshold is downgraded.

## Complete example

### Both implementations in one suite

If the old and new implementations are callable from the same project, a single `BenchmarkSuite` is the simplest approach:

```csharp
var results = await new BenchmarkSuite("parser-refactor")
    .Add("v1-current", () => CurrentParser.Parse(Payload))
    .Add("v2-candidate", () => CandidateParser.Parse(Payload))
    .WithBaseline("v1-current")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

### Old and new in separate classes (cross-class significance)

When legacy and refactored code live in separate classes (for example, in separate projects), use harness mode with the `--cross-class` flag:

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

The console reporter adds a `Class` column to distinguish the rows, and the engine selects the baseline from the entire group.

## What's happening

- **`WithBaseline("name")`**: This designates the reference point. The **Ratio** column shows the speed of other benchmarks relative to the baseline (`0.75x` means 25% faster; `2.0x` means twice as slow), and significance is tested against this baseline. If you do not set a baseline, the benchmark with the lowest median becomes the implicit baseline. For more information, see [Suite mode](../usage-modes/suite-mode.md#setting-a-baseline).

- **Sig**: A **✓** indicates that the difference would occur by chance less than 5% of the time (p < 0.05, two-sided). An **✗** indicates that the measurements are too noisy to conclude one is faster. A blank space indicates the benchmark is the baseline or significance was not tested. For two benchmarks, the engine uses a non-parametric Mann-Whitney U test; for three or more, it uses an omnibus test to gate pairwise comparisons. For more information, see [Significance Testing](../statistics/significance.md).

- **Magnitude**: This classifies the effect size as Negligible, Small, Medium, or Large using Cliff's delta. A statistically significant result (**✓**) with a Negligible magnitude means the difference is real but likely too small to matter. In the console reporter, positive values (candidate slower) are red, and negative values (candidate faster) are green. For more information, see [Reading Your Results: Magnitude](../getting-started/reading-your-results.md#magnitude-suite-mode).

- **`MinimumPracticalEffect`**: This gate (default `0.147`) ensures that a **✓** represents a "real and at least small" effect. Any difference below this threshold is downgraded to `NotSignificant` and its magnitude is forced to `neg`. You can restore p-value-only verdicts by setting `--min-practical-effect 0`, or disable the gate entirely by setting it to `null`. For more information, see [Significance Testing: Practical-significance gate](../statistics/significance.md#practical-significance-gate).

- **Cross-class significance** (`--cross-class` / `WithCrossClassSignificance()`): By default, harness mode computes significance **per class**, where each class has its own baseline. Cross-class mode is an opt-in feature for when classes are competing implementations of the same functionality. For more information, see [Harness mode: Cross-class significance](../usage-modes/harness-mode.md#cross-class-significance).

> [!TIP] Read all three columns together
> A **✓** with a small Ratio (e.g., `1.01x`) and a Negligible Magnitude suggests the difference is real but tiny, and likely not worth the refactor. An **✗** with a large Ratio (e.g., `1.5x`) suggests the measurements are too noisy to be conclusive. To resolve this, reduce noise (see [Tuning for CI/CD pipelines](./ci-cd-pipelines.md)) or collect more samples. The most valuable result is a **✓** with a Small, Medium, or Large Magnitude and a meaningful Ratio.

## Run the benchmark

Execute the following commands based on your mode:

```bash
# Suite mode: Run the comparison
dotnet run -c Release -- --filter "parser-refactor*"

# Harness mode: Run as a cross-class comparison
dotnet run -c Release -- --cross-class

# Demand a tighter confidence interval for publication-grade results
dotnet run -c Release -- --cross-class --auto-tune thorough

# Pin the run for reproducibility across CI and local environments
dotnet run -c Release -- --cross-class --samples 500 --warmup-samples 50 --order declaration
```

## Read the results

A typical verdict for a successful refactor looks like this:

```text
parser-refactor
Benchmark     | Median    | Mean      | Ops/s      | Ratio      | Sig | Mag    | Alloc/op
--------------+-----------+-----------+------------+------------+-----+--------+---------
v1-current    | 420.0 ns  | 422.3 ns  | 2,380,952  | baseline   |  -  |  -     |   128 B
v2-candidate  | 305.0 ns  | 307.1 ns  | 3,278,689  | 0.73x      |  ✓  | large  |    64 B
```

Interpret the results as follows:
- **Ratio `0.73x`**: The candidate is approximately 27% faster.
- **Sig `✓`**: The difference is statistically real and not due to noise.
- **Mag `large`**: The effect size is large, meaning the two distributions barely overlap.
- **Alloc/op `64 B` vs `128 B`**: The candidate halves the allocation, which often explains the speedup.

If the result showed `Sig ✓ | Mag neg`, the difference would be real but below the practical-significance threshold. If it showed `Sig ✗ | Mag large`, the measurements would be too noisy to be conclusive.

For a full explanation of every column, indicator, and warning, see [Reading Your Results](../getting-started/reading-your-results.md).

## Next steps

For more information, see the following pages:

- [Suite mode](../usage-modes/suite-mode.md) - The full fluent API, including `WithSignificance(false)` and `WithSignificanceLevel(alpha)`.
- [Harness mode: Cross-class significance](../usage-modes/harness-mode.md#cross-class-significance) - Details on the `Class` column and baseline selection.
- [Significance Testing](../statistics/significance.md) - Detailed information on Mann-Whitney U, Kruskal-Wallis, and the `MinimumPracticalEffect` gate.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - How to handle cases where `Sig` is `✗` but the Ratio is large.
- [Configuration: Significance](../reference/configuration.md) - The `EnableSignificance`, `SignificanceLevel`, `MinimumPracticalEffect`, and `SignificanceTest` options.
