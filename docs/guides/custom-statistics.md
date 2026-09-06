---
title: Custom statistics
description: The built-in IQR/MAD outlier trimming or rank-based significance tests don't fit your domain - swap in a custom IOutlierDetector and a custom ISignificanceTest.
order: 7
---

# Custom statistics

## Scenario

NBenchmark provides built-in outlier trimming (using an IQR fence by default, or MAD on noisy hosts) and significance testing (using Mann-Whitney U for two groups and Kruskal-Wallis for three or more). While these are designed for general-purpose benchmarking, they may not fit every domain:

- **Latency SLOs**: Service Level Objectives often care about the tail of the distribution rather than the mean. Trimming slow samples before computing statistics hides the exact values a latency budget needs to monitor.
- **Fixed physical thresholds**: Some requirements specify that any sample above a certain limit (e.g., 1 ms) is a stall. These fixed thresholds do not adapt to the data's spread like an IQR fence does.
- **Domain-specific rules**: Some teams prefer comparing medians directly rather than using a distribution-based rank test, as it is simpler and more interpretable.
- **Advanced comparisons**: You may require bootstrap or Bayesian comparisons to obtain a posterior over the difference rather than a single p-value.

Every statistical primitive in NBenchmark is pluggable. Built-in strategies implement the `IOutlierDetector` and `ISignificanceTest` interfaces, allowing you to swap in your own implementation or compose existing ones without forking the engine.

## Complete example

### Custom outlier detector

The following `KeepFastestDetector` preserves the fastest fraction of samples. This is useful for latency-SLO work where the slow tail is the signal rather than noise to be trimmed:

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

### Custom significance test

The following `MedianRatioSignificanceTest` marks a result as significant when the median differs by more than a specified threshold percentage. It does not use p-values or distributional assumptions:

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

In suite mode, use the `WithOutlierDetector` and `WithSignificanceTest` methods:

```csharp
await new BenchmarkSuite("latency-slo")
    .Add("v1", () => CurrentImpl())
    .Add("v2", () => CandidateImpl())
    .WithBaseline("v1")
    .WithOutlierDetector(static () => new KeepFastestDetector(0.90))
    .WithSignificanceTest(static () => new MedianRatioSignificanceTest(thresholdPercent: 25))
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

For single or harness mode, assign the strategies to the `MeasurementOptions` object:

```csharp
new MeasurementOptions
{
    OutlierDetector = static () => new KeepFastestDetector(0.90),
    SignificanceTest = static () => new MedianRatioSignificanceTest(25),
}
```

## What's happening

- **`IOutlierDetector`**: This interface receives a sorted-ascending sample array and returns an `OutlierClassification` containing `Kept`, `Discarded`, and optional `LowerFence` or `UpperFence` values. The contract requires that the detector never discards every sample (return `KeepAll` if the rule would empty the set), does not mutate the input, and returns the `Kept` array sorted ascending. A custom `OutlierDetector` takes priority over the `OutlierMode` setting. The detector's `Name` appears in the report header. For more information, see [Outlier Trimming: Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors).

- **`ISignificanceTest`**: This interface receives a `SignificanceContext` (including the comparable `Groups`, the `BaselineIndex`, the `Baseline` group, the non-baseline `Candidates`, and the `SignificanceLevel`) and returns a `SignificanceReport`. The report contains `Pairwise` comparisons, optional `Effect` metadata, an optional `Shift` estimate, and an optional `Omnibus` verdict. Use `PValue: null` for rules that do not produce a p-value. For more information, see [Significance Testing: Custom significance tests](../statistics/significance.md#custom-significance-tests).

- **The `MinimumPracticalEffect` gate**: The engine enforces this gate in `Significance.ApplyReport` after the test runs. If a custom test returns an `EffectSize` with a `PracticalValue`, it is gated automatically. Tests that do not return a practical value are unaffected. For more information, see [Significance Testing: Practical-significance gate](../statistics/significance.md#practical-significance-gate).

- **Custom statistics in isolated workers**: Strategies are passed to the worker either as a factory or a type name. If a strategy is passed as an instance with constructor arguments, the engine refuses to isolate the run because the instance cannot cross the process boundary. To resolve this, use a factory. In harness mode, scalar CLI overrides (such as iterations, warmup, and confidence) are forwarded to each worker. For more information, see [Isolated runs](../features/isolated-runs.md#additional-details).

- **Worker-side failures**: If a custom strategy fails inside the worker (e.g., because a required file is missing), the engine falls back to the built-in strategy rather than failing the entire run. The engine attaches a warning to each affected result naming the substitution and the reason for the failure.

> [!TIP] Compose built-in strategies
> Built-in strategies - such as `MannWhitneyUSignificanceTest`, `KruskalWallisSignificanceTest`, and `DefaultSignificanceTest` - all implement `ISignificanceTest`. You can wrap or compose these strategies to add a domain-specific gate on top of the standard result, or to fall back to a custom rule when a built-in test returns `NotTested` (e.g., due to too few samples).

## Run the benchmark

Execute the project to see the custom statistics in the report header:

```bash
dotnet run -c Release
```

The output is similar to the following:

```text
latency-slo
Benchmark | Median    | Mean     | Ops/s      | Ratio    | Sig | Mag   | Alloc/op
----------+-----------+----------+------------+----------+-----+-------+---------
v1        | 420.0 ns  | 422.3 ns | 2,380,952  | baseline |  -  |  -    |   128 B
v2        | 305.0 ns  | 307.1 ns | 3,278,689  | 0.73x    |  ✓  | large |    64 B
```

The custom detector and test are named in the header and footer, ensuring the report is self-describing.

## Read the results

The output format remains identical to standard runs. The custom detector and test only change which samples are kept and how significance is determined. For a full explanation of indicators and warnings, see [Reading Your Results](../getting-started/reading-your-results.md).

One caveat: the `Magnitude` column reflects the value returned in `EffectSize.Magnitude`. The built-in tests use labels such as Negligible, Small, Medium, or Large. The console reporter color-codes results based on these conventional labels. To maintain this color coding, use `neg`, `small`, `med`, or `large`.

## Next steps

For more information, see the following pages:

- [Outlier Trimming: Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors) - Full details on the `IOutlierDetector` contract and `OutlierClassification`.
- [Significance Testing: Custom significance tests](../statistics/significance.md#custom-significance-tests) - Full details on the `ISignificanceTest` contract and `SignificanceContext`.
- [Significance Testing: Practical-significance gate](../statistics/significance.md#practical-significance-gate) - How the `MinimumPracticalEffect` gate applies to custom tests.
- [Validation & Accuracy](../statistics/validation.md) - How built-in statistical primitives are cross-validated against SciPy and NumPy.
- [Samples: ExtensibleStats](../samples.md#custom-statistics-sample) - A runnable sample project implementing the custom strategies shown here.
