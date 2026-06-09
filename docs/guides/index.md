---
title: Guides
description: Step-by-step guides for each NBenchmark usage mode.
order: 2
---

# Guides

NBenchmark has three usage modes. Pick the one that matches your situation.

## [Quick mode - Benchmark](./quick-mode.md)

A single static call. No classes, no attributes, no configuration required. Good for a quick measurement anywhere in your code.

```csharp
var result = Benchmark.Run(() => MyMethod());
result.Print();
```

## [Suite mode - BenchmarkSuite](./suite-mode.md)

A fluent builder for comparing multiple implementations. Produces a comparison table with ratios, confidence intervals, and significance testing.

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", BubbleSort)
    .Add("linq",   LinqSort)
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## [Host mode - BenchmarkHost](./host-mode.md)

Attribute-based discovery driven by a command-line interface. Designed for dedicated benchmark projects - similar to BenchmarkDotNet's style.

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

## [Dependency Injection](./dependency-injection.md)

Optional companion package that lets benchmark classes have **constructor dependencies** resolved from an `IServiceProvider`. Adds support for repositories, loggers, `HttpClient`, EF Core `DbContext`, and any other registered service.

```csharp
await BenchmarkHost.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark] public int CountOrders() => repository.Count();
}
```

---

The three modes share the same measurement engine and produce the same `BenchmarkResult` type, so you can mix them in the same project and use the same reporters and configuration across all of them.

## When to switch modes

The modes are designed as an evolutionary path. Start simple, upgrade when your needs grow:

1. **Start with Quick mode** for a one-off measurement - a single `Benchmark.Run` call gives you a statistically rigorous result in three lines of code.

2. **Graduate to Suite mode** when you find yourself writing two `Benchmark.Run` calls to compare an old implementation against a new one. Suite mode handles the comparison automatically - ratios, confidence intervals, and significance testing against a baseline - so you don't have to mentally diff two separate outputs.

3. **Graduate to Host mode** when your suite requires complex setup: mocked databases, loggers, `HttpClient`, or any dependency-injected service. Host mode discovers benchmarks by attribute, parses CLI flags, and supports constructor injection via the optional `NBenchmark.DependencyInjection` package.

Because all three modes produce the same `BenchmarkResult` type, upgrading from one mode to the next is seamless - your reporters, file output, and analysis code work unchanged.
