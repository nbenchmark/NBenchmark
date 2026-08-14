---
title: Getting Started
description: Everything you need to start benchmarking with NBenchmark.
order: 1
---

# Getting Started

Five short pages, about ten minutes end to end. By the end you'll have a benchmark running and you'll
know what its output is telling you.

## In this section

1. **[Installation](./installation.md)** - add the package and verify the setup.
2. **[Choose your path](./choose-your-path.md)** - match what you want to do to the right API.
3. **[Quick Start](./quick-start.md)** - write your first benchmark.
4. **[Reading your results](./reading-your-results.md)** - what every column, indicator, and warning means.
5. **[Key concepts](./key-concepts.md)** - warmup, outliers, confidence, and significance, in plain English.

## In a hurry?

Install the package and run this:

```csharp
using NBenchmark;

Benchmark.Run(() => MyMethod()).Print();
```

Then come back for [Reading your results](./reading-your-results.md).

## Where to go after

- **[Usage modes](../usage-modes/)** - suite mode, harness mode, and the global tool in depth
- **[Guides](../guides/)** - complete recipes for CI gates, refactor comparisons, parameter sweeps
- **[Troubleshooting](../troubleshooting.md)** - when the numbers look wrong
