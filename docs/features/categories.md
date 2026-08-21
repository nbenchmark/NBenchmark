---
title: Categories
description: Tag and filter benchmarks by category.
order: 2
---

# Categories

NBenchmark supports tagging benchmarks with categories to include or exclude them from a run. This is useful for grouping benchmarks by subsystem, speed, or CI tier.

## Tagging benchmarks

Use `[BenchmarkCategory]` on methods, classes, or both. The attribute is repeatable.

```csharp
using NBenchmark.Attributes;

[BenchmarkCategory("String")]
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Fast")]
    public string Concat() => "hello" + " " + "world";

    [Benchmark]
    [BenchmarkCategory("Fast")]
    public string Interpolate() => $"hello {"world"}";

    [Benchmark]
    [BenchmarkCategory("Slow")]
    public string ManyConcat()
    {
        var s = "";
        for (var i = 0; i < 100; i++)
            s += (char)('a' + i % 26);
        return s;
    }
}
```

The engine unions class-level categories with method-level categories. For example, `ManyConcat` is tagged with both `String` and `Slow`. The engine also applies inherited class-level categories to derived classes.

## CLI filtering

The following flags control category filtering from the command line:

| Flag | Description |
| --- | --- |
| `--category <name>` | Includes benchmarks tagged with this category. This flag is repeatable (OR logic). |
| `--exclude-category <name>` | Excludes benchmarks tagged with this category. This flag is repeatable (OR logic). |

```bash
# Run all String benchmarks
dotnet run -- --category String

# Run fast string benchmarks only
dotnet run -- --category String --exclude-category Slow

# Run benchmarks tagged String OR Memory
dotnet run -- --category String --category Memory

# Combine with the glob filter
dotnet run -- --category String --filter StringBenchmarks.Con*
```

If any `--category` flag is present, the engine excludes untagged benchmarks.

## Programmatic filtering

In Harness mode, use `WithCategoryFilter`:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithCategoryFilter(include: ["String"], exclude: ["Slow"])
    .RunAsync();
```

In Suite mode, use `WithCategories` and `WithCategoryFilter`:

```csharp
var results = await new BenchmarkSuite("string")
    .Add("concat", () => "a" + "b", categories: ["Fast"])
    .Add("interpolate", () => $"a { "b" }", categories: ["Fast"])
    .Add("manyConcat", () => string.Concat(Enumerable.Range(0, 100)))
    .WithCategoryFilter(include: ["Fast"])
    .RunAsync();
```

`WithCategoryFilter` composes with CLI flags: each include source must match independently, while exclude lists are unioned. This allows you to set a default include list in code and still narrow it from the command line.

## Categories in reports

- **JSON** always emits a `categories` array on every `BenchmarkResult`.
- **Markdown**, **CSV**, and **Console** reporters show a `Categories` column only in **advanced** detail.
- The `--list` flag prints categories next to each benchmark when any are present.

```bash
dotnet run -- --list
dotnet run -- --reporter markdown --detail advanced --output ./results
```

## See also

For more information, see the following pages:

- [Parameterized benchmarks: Suite mode](./parameterized-suite.md) - How categories combine with parameter sweeps.
- [Parameterized benchmarks: Harness mode](./parameterized-harness.md) - Using `[BenchmarkCategory]` on attribute-discovered methods.
- [CLI Reference: `--category` / `--exclude-category`](../reference/cli.md#selection) - Details on the CLI filter flags.
