---
title: What's Next?
description: Where to go after the Quick Start.
order: 4
---

# What's Next?

Now that you've run your first benchmark, here's where to go depending on what you want to do.

## I want to compare multiple implementations

Use **Suite mode: BenchmarkSuite** - a fluent builder for running several benchmarks side-by-side with a comparison table. When your suite grows to need complex setup or dependency injection, graduate to **Host mode**.

→ [Suite mode](../usage-modes/suite-mode.md)

## I want a dedicated benchmark project with attribute-based discovery

Use **Host mode: BenchmarkHost** - mark methods with `[Benchmark]`, point the host at your assembly, and control everything from the command line.

→ [Host mode](../usage-modes/host-mode.md)

## I want richer terminal output

Add `NBenchmark.Reporters.Console` and use `ConsoleReporter`. It produces a colour-coded table with a bar chart, significance indicators, and a footnote explaining the Error column.

→ [ConsoleReporter](../output/console-reporter.md)

## I want to save results to a file

Use `MarkdownReporter`, `CsvReporter`, or `JsonReporter`. They require only the core `NBenchmark` package and can be stacked with any other reporter.

→ [Reporters](../output/index.md)

## I want to tune the measurement settings

Change the number of iterations, warmup iterations, outlier mode, or confidence level.

→ [Configuration](../reference/configuration.md)

## I want to understand the statistics

A full technical explanation of how every number in the output is calculated.

→ [Statistics](../statistics/)

## I want to run benchmarks from the command line without recompiling

Use the `dotnet benchmark` global tool. It wraps Host mode into a single command, so you can run `[Benchmark]` methods from an existing assembly without creating a dedicated benchmark project.

→ [Global Tool: dotnet benchmark](../usage-modes/global-tool.md)

## My benchmark class needs dependencies (a repository, `DbContext`, logger, etc.)

Add the optional `NBenchmark.DependencyInjection` companion package and use `UseDependencyInjection<T>` - your benchmark class can then take constructor dependencies that the container resolves.

→ [Dependency Injection guide](../features/dependency-injection.md)

## I want performance checks to run as part of my existing test suite

Use one of the test framework integration packages. Replace your test attribute with the corresponding performance attribute, set threshold values as named arguments, and the benchmark runs when your tests run.

→ [Test integration](../test-integration/index.md)
