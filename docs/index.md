---
title: NBenchmark
description: A lightweight, async-native .NET benchmarking library with a great developer experience.
order: 0
---

# NBenchmark

NBenchmark is a lightweight benchmarking library for .NET. It is designed around three principles:

- **Excellent developer experience.** Go from nothing to your first measurement in one line of code.
- **High performance.** No reflection overhead in the measurement loop, accurate timers, proper GC handling.
- **Statistically honest output.** Confidence intervals, outlier trimming, and a [non-parametric significance test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) are on by default so you know whether a difference is real.

## Packages

| Package | Description |
|---|---|
| `NBenchmark` | Zero-dependency core - all measurement, statistics, and file reporters. |
| `NBenchmark.Analyzers` | Roslyn analyzers that catch common benchmark authoring mistakes at compile time. See the [Analyzers page](./analyzers.md) for the full diagnostic list. |
| `NBenchmark.DependencyInjection` | Resolves benchmark classes from an `IServiceProvider` so they can have constructor dependencies. |
| `NBenchmark.Reporters.Console` | Adds a rich terminal table via [Spectre.Console](https://spectreconsole.net/). |
| `NBenchmark.Integration.xUnit` | Run NBenchmark benchmarks as xUnit tests with configurable performance thresholds. |
| `NBenchmark.Integration.NUnit` | Run NBenchmark benchmarks as NUnit tests with configurable performance thresholds. |
| `NBenchmark.Integration.MSTest` | Run NBenchmark benchmarks as MSTest tests with configurable performance thresholds. |

## Pick a starting point

Not sure where to begin? Start here:

- **[Installation](./getting-started/installation.md)** - add the NuGet packages
- **[Quick Start](./getting-started/quick-start.md)** - your first benchmark in 60 seconds
- **[Key Concepts](./getting-started/key-concepts.md)** - what warmup, outliers, and the Error column mean

Already comfortable with the basics?

- **[Guides](./guides/)** - detailed walkthroughs for each usage mode
- **[Dependency Injection](./guides/dependency-injection.md)** - benchmark classes with constructor dependencies
- **[Integration](./integration/)** - enforce performance thresholds as xUnit, NUnit, or MSTest tests, and more
- **[Configuration](./configuration.md)** - every option explained
- **[CLI Reference](./cli-reference.md)** - all command-line flags for `BenchmarkHost`
- **[Analyzers](./analyzers.md)** - compile-time diagnostics for NBenchmark (NB0001-NB0010)
- **[Advanced: Statistics](./advanced/statistics.md)** - how the numbers are calculated
