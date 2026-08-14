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

- **Magnitude** - classifies the effect size as Negligible / Small / Medium / Large from Cliff's delta. A statistically significant result (✓) with a Negligible magnitude means the difference is real but too small to care about. Positive = candidate slower (red in the console); negative = candidate faster (green). See [Reading Your Results: Magnitude](../getting-started/reading-your-results.md#magnitude-suite-mode).

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

See [Reading Your Results](../getting-started/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API, including `WithSignificance(false)` to disable significance and `WithSignificanceLevel(alpha)` to demand stronger evidence.
- [Harness mode: Cross-class significance](../usage-modes/harness-mode.md#cross-class-significance) - the `Class` column, baseline selection, when to use cross-class vs. per-class.
- [Significance Testing](../statistics/significance.md) - the Mann-Whitney U and Kruskal-Wallis algorithms, the Holm-Bonferroni correction, Cliff's delta thresholds, and the `MinimumPracticalEffect` gate in detail.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - what to do when `Sig` is `✗` and the Ratio is large (the noise problem).
- [Configuration: Significance](../reference/configuration.md) - the `EnableSignificance`, `SignificanceLevel`, `MinimumPracticalEffect`, and `SignificanceTest` options.
