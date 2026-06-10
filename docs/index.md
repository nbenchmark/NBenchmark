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
| `NBenchmark` | The zero-dependency core. All measurement, statistics, and file reporters. |
| `NBenchmark.Reporters.Console` | Adds a rich terminal table and progress display via Spectre.Console. |
| `NBenchmark.Extensions.DependencyInjection` | Optional integration that lets `[Benchmark]` classes have constructor dependencies resolved from an `IServiceProvider`. |
| `NBenchmark.Analyzers` | Roslyn analyzers that catch common configuration errors at compile time. See the [Analyzers page](./analyzers.md) for the full diagnostic list. |

## Pick a starting point

Not sure where to begin? Start here:

- **[Installation](./getting-started/installation.md)** - add the NuGet packages
- **[Quick Start](./getting-started/quick-start.md)** - your first benchmark in 60 seconds
- **[Key Concepts](./getting-started/key-concepts.md)** - what warmup, outliers, and the Error column mean

Already comfortable with the basics?

- **[Guides](./guides/)** - detailed walkthroughs for each usage mode
- **[Dependency Injection](./guides/dependency-injection.md)** - benchmark classes with constructor dependencies
- **[Configuration](./configuration.md)** - every option explained
- **[CLI Reference](./cli-reference.md)** - all command-line flags for `BenchmarkHost`
- **[Analyzers](./analyzers.md)** - compile-time diagnostics for NBenchmark (NB0001-NB0010)
- **[Advanced: Statistics](./advanced/statistics.md)** - how the numbers are calculated
