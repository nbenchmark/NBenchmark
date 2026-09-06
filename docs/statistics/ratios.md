---
title: Ratios
description: How the Ratio column is estimated, why it is paired across launches, and how to read its interval.
order: 5
---

# Ratios

The `Ratio` column answers the question: "How much slower than the baseline is this benchmark?" When a run includes more than one launch, the ratio also indicates whether the difference is real or if it would likely change upon re-running.

## Paired ratios vs. quotients of medians

The simplest way to compute a ratio is to divide the candidate's median by the baseline's. NBenchmark only uses this method for single-launch runs.

When `LaunchCount > 1`, NBenchmark computes the ratio **per launch** and then combines those results:

```text
launch 0:  candidate 150 ns / baseline 100 ns  = 1.50x
launch 1:  candidate 300 ns / baseline 200 ns  = 1.50x
launch 2:  candidate 600 ns / baseline 400 ns  = 1.50x
                                        ratio  = 1.50x  [1.50-1.50x]
```

In the example above, the launches differ from each other by 4$\times$. Dividing the aggregated medians would incorporate this noise into the comparison. However, because a comparison group is measured co-resident in one worker process per launch, the candidate and baseline share the same core draw, thermal state, and address-space layout. Dividing them per launch cancels these factors.

This co-residency is why NBenchmark runs a whole group in one worker rather than isolating each benchmark separately. Dividing two aggregated medians would discard this benefit.

## The log scale

Ratios are multiplicative. On a linear scale, "2$\times$ slower" and "2$\times$ faster" sit at +1.0 and -0.5. An average of these two would result in 1.25$\times$, creating a fabricated 25% slowdown from two measurements that exactly cancel each other.

To avoid this, NBenchmark averages the **logarithms** of the per-launch ratios - making those two symmetric (+0.69 and -0.69) - and then exponentiates the result. This produces the **geometric mean**, where the average of 0.5$\times$ and 2.0$\times$ is 1.00$\times$.

This approach has two consequences:
- **Multiplicative symmetry**: The interval is symmetric such that `value / lower` equals `upper / value`.
- **Estimate difference**: The point estimate is not the quotient of the aggregated medians; it is an estimate that accounts for the pairing.

## Reading the interval

| Location | Indicator |
|---|---|
| Console | `1.24x` - A `?` suffix and dim color indicate the interval spans `1.00x`. |
| Markdown | A `Ratio CI` column, marked with ⚠️ when the interval spans `1.00x`. |
| CSV | `RatioCiLower`, `RatioCiUpper`, and `RatioReplicates` columns. |
| Advanced Detail | A `Ratio:` line in the per-benchmark stats block. |

**An interval that spans `1.00x` means the run cannot distinguish between the two benchmarks**, regardless of where the point estimate sits. For example, a `1.35x` ratio with an interval of `0.82-2.24x` is not a 35% regression; it is a result that lacks sufficient replicates to be conclusive. To narrow the interval, increase the `--launch-count`.

## When Sig and the ratio interval disagree

The `Sig` column and the ratio interval are different tests. NBenchmark warns you when they conflict:

| Metric | Computed from | Purpose |
|---|---|---|
| `Sig` (✓/✗) | Samples **pooled across all launches** | Determines if the difference is unlikely under the null hypothesis. |
| `Ratio CI` | The **per-launch ratios** | Determines if the ratio would reproduce on a re-run. |

A pooled sample count provides statistical power regardless of reproducibility. Therefore, a difference far below the run-to-run noise can still be marked as significant. When a row is marked **✓** but its ratio interval spans `1.00x`, **trust the interval**.

The console reports this explicitly:

```text
Warning: 1 row(s) are marked significant (✓) yet their ratio interval spans 1.00x.
Significance is computed on samples pooled across launches, where a large count
grants power regardless of reproducibility; the ratio interval is the run-to-run
spread. Trust the interval.
```

## What pairing does not fix

Pairing removes the worker's own CPU draw and memory layout from the ratio. However, it does **not** make different runtime configurations comparable. For instance, measuring one body with tiered compilation off and another under the host's inherited configuration can result in a 3.3$\times$ difference for bodies of identical cost. Such ratios are reported as `n/a`. For more information, see [Isolated runs](../features/isolated-runs.md).

## The threshold gate

The `--max-regression-percent` flag applies its percentage to the paired ratio when launches are available. This ensures the CI gate compares the code rather than the quietest core. `RegressionCandidate.Estimate` carries the interval, allowing consumers to report whether a failure is supported by the data.

## Test-framework gates

A `[Performance]` test measures one launch by default, resulting in a quotient of medians with no interval. Setting `LaunchCount` on the attribute provides the same paired estimate the engine uses:

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(Naive), LaunchCount = 3)]
public void Optimized() => Optimized.Parse(Payload);
```

The candidate and its reference are measured **co-resident in one worker per replicate**. This costs three launches instead of six.

When an interval is present, the gate changes its logic: the threshold applies to the paired estimate, and a failure is triggered only if the interval excludes `1.00x`. A ratio that exceeds the threshold but has an interval spanning `1.00x` is **not** enforced. In calibration mode (`MaxSlowdownRatio` with no `ReferenceMethod`), each replicate's worker measures the calibration standard after its own benchmark work, and those medians are paired by launch.

For more information, see [Test integration](../test-integration/index.md#replicates-and-the-paired-ratio).

## See also

- [Multiple launches](../features/multiple-launches.md) - How `LaunchCount` works and how launches are combined.
- [Significance testing](significance.md) - Details on the `Sig` and `Magnitude` columns.
- [Isolated runs](../features/isolated-runs.md) - Why a comparison group shares one worker.
