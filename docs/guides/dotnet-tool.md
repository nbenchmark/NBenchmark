---
title: "Global Tool: dotnet benchmark"
description: Run benchmarks from the command line without creating a project. Install once, benchmark any assembly.
order: 4
---

# Global Tool: dotnet benchmark

The `dotnet benchmark` global tool wraps `BenchmarkHost` into a single command. Install it once, then run benchmarks against any .NET assembly without creating a dedicated host project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark
```

## When to use the tool

The tool replaces Host mode when you want to benchmark an existing project without adding a `Program.cs`, `Main`, and NuGet references. It is the fastest path from "I have a project with `[Benchmark]` methods" to "I have results."

| You want to... | Use |
|---|---|
| Benchmark a project you already built | `dotnet benchmark` in the output directory |
| Build and benchmark in one step | `dotnet benchmark --project ./MyBenchmarks` |
| Benchmark a specific assembly | `dotnet benchmark --assembly ./bin/Release/net10.0/MyLib.dll` |
| Filter, configure output, set thresholds | All `--filter`, `--reporter`, `--output`, `--threshold-pct` flags work |

## Installation

```bash
dotnet tool install -g NBenchmark.Tool
```

Verify it works:

```bash
dotnet benchmark --help
```

To update:

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

Assemblies without `[Benchmark]` methods are skipped silently.

### --project: build and benchmark

Pass a `.csproj` path (or a directory containing one). The tool runs `dotnet build -c Release`, finds the output assembly, and benchmarks it.

```bash
dotnet benchmark --project ./MyApp.Benchmarks/MyApp.Benchmarks.csproj
dotnet benchmark --project ./MyApp.Benchmarks   # same, if only one .csproj
```

### --assembly: explicit assembly path

Pass one or more `.dll` paths directly. Repeatable.

```bash
dotnet benchmark --assembly ./Lib1.dll --assembly ./Lib2.dll
```

## All host flags pass through

Every flag supported by `BenchmarkHost` works unchanged:

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

See the [CLI reference](../reference/cli.md) for the full flag list.

## Default reporter

When no `--reporter` flag is given, the tool adds the console reporter automatically so you see results in the terminal. Pass any `--reporter` flag to override this default.

```bash
dotnet benchmark                              # console output
dotnet benchmark --reporter json              # JSON file only
dotnet benchmark --reporter json --reporter markdown  # both files
```

## Process isolation

The tool inherits Host mode's isolated-by-default execution. Each benchmark class runs in a clean child process unless you pass `--in-process`.

```bash
dotnet benchmark                              # isolated (default)
dotnet benchmark --in-process                 # all in-process
```

When using `--project`, the tool forwards the built benchmark assembly paths to child processes automatically, so isolated runs work from any working directory.

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

- [Host mode](./host-mode.md) - the project-based alternative
- [CLI reference](../reference/cli.md) - all available flags
- [Reporters](../reporters/index.md) - output formats
- [Configuration](../reference/configuration.md) - measurement options
