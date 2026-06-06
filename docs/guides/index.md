---
title: Guides
description: Step-by-step guides for each NBenchmark usage tier.
order: 2
---

# Guides

NBenchmark has three usage tiers. Pick the one that matches your situation.

## [Tier 1 — Bench](./tier-1-bench)

A single static call. No classes, no attributes, no configuration required. Good for a quick measurement anywhere in your code.

```csharp
var result = Bench.Time(() => MyMethod());
result.Print();
```

## [Tier 2 — BenchmarkSuite](./tier-2-suite)

A fluent builder for comparing multiple implementations. Produces a comparison table with ratios, confidence intervals, and significance testing.

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", BubbleSort)
    .Add("linq",   LinqSort)
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## [Tier 3 — BenchmarkHost](./tier-3-host)

Attribute-based discovery driven by a command-line interface. Designed for dedicated benchmark projects — similar to BenchmarkDotNet's style.

```csharp
await BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    [Benchmark]
    public int Compute() => SomeExpensiveWork();
}
```

---

The three tiers share the same measurement engine and produce the same `BenchmarkResult` type, so you can mix them in the same project and use the same reporters and configuration across all of them.
