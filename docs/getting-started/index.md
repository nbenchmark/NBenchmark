---
title: Getting Started
description: Everything you need to start benchmarking with NBenchmark.
order: 1
---

# Getting Started

This section provides everything you need to start benchmarking with NBenchmark. You can complete these five pages in about ten minutes. By the end, you'll have a benchmark running and understand how to interpret the results.

## In this section

1. [Installation](./installation.md) - Add the package and verify your setup.
2. [Choose your path](./choose-your-path.md) - Match your goals to the appropriate API.
3. [Quick start](./quick-start.md) - Write your first benchmark.
4. [Reading your results](./reading-your-results.md) - Understand every column, indicator, and warning.
5. [Key concepts](./key-concepts.md) - Learn about warmup, outliers, confidence, and significance.

## Quick start example

Install the package and use the `Benchmark.Run` method to measure a single method:

```csharp
using NBenchmark;

Benchmark.Run(() => MyMethod()).Print();
```

For more information on how to interpret the output, see [Reading your results](./reading-your-results.md).

## Next steps

For more advanced usage, see the following sections:

- [Usage modes](../usage-modes/) - Detailed information about suite mode, harness mode, and the global tool.
- [Guides](../guides/) - Complete recipes for CI gates, refactor comparisons, and parameter sweeps.
- [Troubleshooting](../troubleshooting.md) - How to handle unexpected or incorrect results.
