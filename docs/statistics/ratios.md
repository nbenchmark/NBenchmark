---
title: Ratios
description: How the Ratio column is estimated, why it is paired across launches, and how to read its interval.
---

# Ratios

The `Ratio` column answers "how much slower than the baseline?". When a run has more than one launch, it also answers a second question the number alone cannot: **is that difference real, or would it move if you ran again?**

## The ratio is paired, not a quotient of two medians

The obvious way to compute a ratio is to divide the candidate's median by the baseline's. NBenchmark does that only when it has to - a single-launch run.

With `LaunchCount > 1` it computes the ratio **per launch** and combines those:

```text
launch 0:  candidate 150 ns / baseline 100 ns  = 1.50x
launch 1:  candidate 300 ns / baseline 200 ns  = 1.50x
launch 2:  candidate 600 ns / baseline 400 ns  = 1.50x
                                       ratio  = 1.50x  [1.50-1.50x]
```

Those three launches disagree with each other by 4x. Divide the aggregated medians and that 4x sits in the numerator and denominator independently, contributing noise to a comparison that - launch by launch - is exactly 1.50x every time.

Pairing works because **a comparison group is measured co-resident in one worker process per launch**. Launch 1 of the candidate and launch 1 of the baseline ran in the same process, on the same core draw, under the same thermal state, with the same page and address-space layout. Dividing them cancels all of that. That co-residency is the reason NBenchmark runs a whole group in one worker rather than isolating each benchmark separately - and dividing two aggregated medians throws the benefit away at the last step.

## Why the log scale

Ratios are multiplicative. On a linear scale "2x slower" and "2x faster" sit at +1.0 and -0.5, so an average of the two lands at 1.25x - a fabricated 25% slowdown out of two measurements that cancel exactly.

NBenchmark averages the **logarithms** of the per-launch ratios, which makes those two symmetric (+0.69 and -0.69), then exponentiates. The result is the **geometric mean**, and the average of 0.5x and 2.0x is 1.00x as it should be.

Two consequences worth knowing:

- The interval is **multiplicatively symmetric**: `value / lower` equals `upper / value`. A linear interval on a ratio with real spread routinely produces a negative lower bound, and a negative ratio is not something a benchmark can produce.
- The point estimate is not the quotient of the aggregated medians. Where the two disagree, the paired one is the estimate that accounts for the pairing.

## Reading the interval

| Where it appears | How |
| --- | --- |
| Console | `1.24x` - a `?` suffix and a dim colour when the interval spans `1.00x` |
| Markdown | a `Ratio CI` column, marked ⚠️ when it spans `1.00x` |
| CSV (Standard, Advanced) | `RatioCiLower`, `RatioCiUpper`, `RatioReplicates` |
| Console / Markdown, `--detail advanced` | a `Ratio:` line in the per-benchmark stats block |

**An interval spanning `1.00x` means this run cannot distinguish the two benchmarks** - however far the point estimate sits from 1.00. A `1.35x` whose interval is `0.82-2.24x` is not a 35% regression; it is a run that does not have the replicates to say. Raise `--launch-count` to narrow it.

## When Sig and the ratio interval disagree

The `Sig` column and the ratio interval are different tests, and NBenchmark warns when they conflict:

|  | computed from | answers |
| --- | --- | --- |
| `Sig` (✓/✗) | samples **pooled across all launches** | is this difference unlikely under the null? |
| `Ratio CI` | the **per-launch ratios** | would this ratio reproduce on a re-run? |

A pooled sample count buys statistical power regardless of reproducibility, so a difference far below the run-to-run noise can still read as overwhelmingly significant. When a row is marked ✓ and its ratio interval spans `1.00x`, **trust the interval** - the console says so explicitly:

```text
Warning: 1 row(s) are marked significant (✓) yet their ratio interval spans 1.00x.
Significance is computed on samples pooled across launches, where a large count
grants power regardless of reproducibility; the ratio interval is the run-to-run
spread. Trust the interval.
```

This is not a hypothetical. On this repository's own calibration sample - four benchmark bodies of provably identical cost - a run regularly marks one of them significant against another while the paired interval correctly reports no measured difference.

## What pairing does not fix

Pairing removes a worker's own CPU draw and memory layout from the ratio. It does **not** make two different runtime configurations comparable: measuring one body with tiered compilation off and another under the host's inherited configuration is worth roughly 3.3x on bodies of identical cost. Those ratios stay withheld as `n/a` regardless of how many launches were paired. See [Isolated runs](../features/isolated-runs.md).

## The threshold gate

`--threshold-pct` applies its percentage to the paired ratio when the run had launches to pair, so a CI gate compares code rather than whichever worker drew the quietest core. `RegressionCandidate.Estimate` carries the interval, so a consumer building an alert can report whether a failure is supported by the data.

## Test-framework gates

A `[Performance]` test measures one launch by default, so its ratio is a quotient with no interval - all a single measurement can support. Setting `LaunchCount` on the attribute buys the same paired estimate the engine uses:

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(Naive), LaunchCount = 3)]
public void Optimised() => Optimised.Parse(Payload);
```

The candidate and its reference are measured **co-resident in one worker per replicate**, not in a worker each, so the pairing is the same within-process pairing described above - and it costs three launches rather than six.

With an interval present the gate changes what it turns on: the threshold applies to the paired estimate, and "is the difference real" becomes "does the interval exclude `1.00x`" rather than a p-value over pooled samples. A ratio past the threshold whose interval spans `1.00x` is **not** enforced, and the test output says so rather than passing in silence. In calibration mode - `MaxSlowdownRatio` with no `ReferenceMethod` - each replicate's worker measures the calibration standard after its own benchmark work, so those medians pair by launch too.

See [Test integration](../test-integration/index.md#replicates-and-the-paired-ratio).

## See also

- [Multiple launches](../features/multiple-launches.md) - what `LaunchCount` buys and how the launches are combined
- [Significance testing](significance.md) - the `Sig` and `Magnitude` columns
- [Isolated runs](../features/isolated-runs.md) - why a comparison group shares one worker
