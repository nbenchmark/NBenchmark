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

- **Isolated workers preserve your custom statistics, and say so when they cannot.** A strategy reaches the worker either as a factory it runs or as a type name it constructs, so the object that scores an isolated run is your own. Where that is not possible the coordinator declines to isolate rather than substitute quietly - a strategy built with constructor arguments and passed as an *instance* is refused, because only a type name would cross and only a parameterless constructor could be reached at the other end. Pass a factory instead and the argument travels with it. In Harness mode, scalar CLI overrides (iterations, warmup, confidence, etc.) are forwarded to each worker. See [Isolated runs](../features/isolated-runs.md#important-behavior-notes).

- **A strategy that fails inside the worker warns on every result it scored.** Whether a factory *works* is not knowable until it runs in the process that measures - it may read a file that is not deployed beside the worker, or need a type that will not load there. The group is still measurable, so the engine falls back to its built-in strategy rather than failing the run, and attaches a warning to each affected result naming what was substituted and why. Read it: those numbers were scored by a method you did not choose.

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
