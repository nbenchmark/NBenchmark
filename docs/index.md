---
title: NBenchmark
description: A lightweight, async-native .NET benchmarking library with a great developer experience.
order: 0
---

# NBenchmark

NBenchmark is a lightweight benchmarking library for .NET. It is designed around three principles:

- **Excellent developer experience.** Go from nothing to your first measurement in one line of code.
- **High performance.** No reflection overhead in the measurement loop, accurate timers, proper GC handling.
- **Statistically honest output.** Confidence intervals, outlier trimming, and a non-parametric significance test are on by default so you know whether a difference is real.

## Packages

| Package | Description |
|---|---|
| `NBenchmark` | The zero-dependency core. All measurement, statistics, and file reporters. |
| `NBenchmark.Console` | Adds a rich terminal table and progress display via Spectre.Console. |

## Pick a starting point

Not sure where to begin? Start here:

- **[Installation](./getting-started/installation)** — add the NuGet packages
- **[Quick Start](./getting-started/quick-start)** — your first benchmark in 60 seconds
- **[Key Concepts](./getting-started/key-concepts)** — what warmup, outliers, and the Error column mean

Already comfortable with the basics?

- **[Guides](./guides/)** — detailed walkthroughs for each usage tier
- **[Configuration](./configuration)** — every option explained
- **[CLI Reference](./cli-reference)** — all command-line flags for `BenchmarkHost`
- **[Advanced: Statistics](./advanced/statistics)** — how the numbers are calculated

> **Pre-release.** NBenchmark targets .NET 10 and is under active development. The API may change between versions.
