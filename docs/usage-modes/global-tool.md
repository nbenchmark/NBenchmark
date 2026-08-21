---
title: "Global Tool: dotnet benchmark"
description: Run benchmarks from the command line without creating a project. Install once, benchmark any assembly.
order: 4
---

# Global Tool: dotnet benchmark

The `dotnet benchmark` global tool wraps `BenchmarkHarness` into a single command. Install it once to run benchmarks against any .NET assembly without creating a dedicated host project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark
```

## When to use the tool

The tool replaces Harness mode when you want to benchmark an existing project without adding a `Program.cs` file, `Main` method, or NuGet references. It provides the fastest path from having a project with `[Benchmark]` methods to obtaining results.

| If you want to... | Use... |
| --- | --- |
| Benchmark a project you already built | `dotnet benchmark` in the output directory |
| Build and benchmark in one step | `dotnet benchmark --project ./MyBenchmarks` |
| Benchmark a specific assembly | `dotnet benchmark --assembly ./bin/Release/net10.0/MyLib.dll` |
| Filter, configure output, or set thresholds | All `--filter`, `--reporter`, `--output`, and `--threshold-pct` flags |

## Installation

Install the tool using the following command:

```bash
dotnet tool install -g NBenchmark.Tool
```

Verify the installation:

```bash
dotnet benchmark --help
```

To update the tool:

```bash
dotnet tool update -g NBenchmark.Tool
```

## Discovery modes

The tool finds benchmarks using one of three strategies.

### Default: scan the current directory

Run `dotnet benchmark` in a directory containing compiled `.dll` files. The tool loads each `.dll`, checks for `[Benchmark]` methods, and runs any it finds.

```bash
cd ./MyApp/bin/Release/net10.0
dotnet benchmark
```

The tool silently skips assemblies without `[Benchmark]` methods.

### --project: build and benchmark

Pass a `.csproj` path or a directory containing one. The tool runs `dotnet build -c Release`, finds the output assembly, and benchmarks it.

```bash
dotnet benchmark --project ./MyApp.Benchmarks/MyApp.Benchmarks.csproj
dotnet benchmark --project ./MyApp.Benchmarks   # Works if the directory contains only one .csproj
```

### --assembly: explicit assembly path

Pass one or more `.dll` paths directly. You can repeat the `--assembly` flag for multiple files.

```bash
dotnet benchmark --assembly ./Lib1.dll --assembly ./Lib2.dll
```

## Host flags

Every flag supported by `BenchmarkHarness` works unchanged:

```bash
dotnet benchmark --filter "*Sort*"
dotnet benchmark --reporter json --output ./results
dotnet benchmark --iterations 500 --warmup 50
dotnet benchmark --detail advanced
dotnet benchmark --threshold-pct 20
dotnet benchmark --list
dotnet benchmark --dry-run
dotnet benchmark --in-process
```

For the full list of flags, see the [CLI reference](../reference/cli.md).

## Default reporter

When you do not provide a `--reporter` flag, the tool automatically adds the console reporter so you can see results in the terminal. Use any `--reporter` flag to override this default.

```bash
dotnet benchmark                              # Console output
dotnet benchmark --reporter json              # JSON file only
dotnet benchmark --reporter json --reporter markdown  # Both files
```

## Process isolation

The tool inherits the isolated-by-default execution of Harness mode. Each benchmark class runs in a clean worker unless you pass `--in-process`.

```bash
dotnet benchmark                              # Isolated (default)
dotnet benchmark --in-process                 # All in-process
```

The tool itself does not perform measurements. It loads your assembly to discover benchmarks and then hands each class to a worker; this ensures isolated runs work regardless of your working directory.

If a run reports every benchmark as `in-process (no worker)`, the target project was likely built without the worker. Ensure the project references the `NBenchmark` package and has not set `NBenchmarkDeployWorker=false`. For more information, see [Troubleshooting](../troubleshooting.md#could-not-load-file-or-assembly-in-aspnet-core-or-wpf).

## ASP.NET Core and WPF projects

These projects work without additional configuration. The tool detects if your assembly requires a shared framework that it was not started with and restarts itself under the correct framework before reading the assembly.

The only exception is a **self-contained** target, where the framework lives in its own output directory rather than a shared location. In this case, build the benchmark project as framework-dependent or pass `--in-process`. For a full explanation, see [Troubleshooting](../troubleshooting.md#could-not-load-file-or-assembly-in-aspnet-core-or-wpf). For details on how the framework set is resolved, see [Isolation internals](../deep-dives/isolation-internals.md#shared-framework-configuration).

## Examples

### Quick check on a library

```bash
cd ./MyApp/bin/Release/net10.0
dotnet benchmark --filter "*Parse*"
```

### Full CI gate

```bash
dotnet benchmark --project ./MyApp.Benchmarks \
  --reporter json --output ./bench-results \
  --threshold-pct 10
```

### Compare two builds

```bash
# Before
dotnet benchmark --assembly ./old/MyApp.dll --reporter json --output ./before

# After
dotnet benchmark --assembly ./new/MyApp.dll --reporter json --output ./after
```

## See also

- [Harness mode](./harness-mode.md) - The project-based alternative
- [CLI reference](../reference/cli.md) - All available flags
- [Reporters](../output/index.md) - Output formats
- [Configuration](../reference/configuration.md) - Measurement options
