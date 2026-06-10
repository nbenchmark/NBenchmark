---
title: What's Next?
description: Where to go after the Quick Start.
order: 4
---

# What's Next?

Now that you've run your first benchmark, here's where to go depending on what you want to do.

## I want to compare multiple implementations

Use **Suite mode: BenchmarkSuite** - a fluent builder for running several benchmarks side-by-side with a comparison table. When your suite grows to need complex setup or dependency injection, graduate to **Host mode**.

→ [Guide: BenchmarkSuite](../guides/suite-mode.md)

## I want a dedicated benchmark project with attribute-based discovery

Use **Host mode: BenchmarkHost** - mark methods with `[Benchmark]`, point the host at your assembly, and control everything from the command line.

→ [Guide: BenchmarkHost](../guides/host-mode.md)

## I want richer terminal output

Add `NBenchmark.Reporters.Console` and use `ConsoleReporter`. It produces a colour-coded table with a bar chart, significance indicators, and a footnote explaining the Error column.

→ [ConsoleReporter](../reporters/console.md)

## I want to save results to a file

Use `MarkdownReporter`, `CsvReporter`, or `JsonReporter`. They require only the core `NBenchmark` package and can be stacked with any other reporter.

→ [Reporters](../reporters/)

## I want to tune the measurement settings

Change the number of iterations, warmup iterations, outlier mode, or confidence level.

→ [Configuration](../configuration.md)

## I want to understand the statistics

A full technical explanation of how every number in the output is calculated.

→ [Advanced: Statistics](../advanced/statistics.md)

## I want to run benchmarks from the command line without recompiling

Use `BenchmarkHost`, which parses CLI arguments automatically. You can filter benchmarks, change reporter, set output directories, and more.

→ [CLI Reference](../cli-reference.md)

## My benchmark class needs dependencies (a repository, `DbContext`, logger, etc.)

Add the optional `NBenchmark.Extensions.DependencyInjection` companion package and use `UseDependencyInjection<T>` - your benchmark class can then take constructor dependencies that the container resolves.

→ [Dependency Injection guide](../guides/dependency-injection.md)
