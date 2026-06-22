---
title: Categories
description: Tag and filter benchmarks by category.
order: 3
---

# Categories

NBenchmark supports tagging benchmarks with categories and then including or excluding them from a run. This is useful for grouping benchmarks by subsystem, speed, or CI tier.

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

Class-level categories are unioned with method-level categories, so `ManyConcat` is tagged with both `String` and `Slow`. Inherited class-level categories are also applied to derived classes.

## CLI filtering

| Flag | Description |
|---|---|
| `--category <name>` | Include benchmarks tagged with this category. Repeatable (OR). |
| `--exclude-category <name>` | Exclude benchmarks tagged with this category. Repeatable (OR). |

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

Untagged benchmarks are excluded when any `--category` flag is present.

## Programmatic filtering

In Host mode, use `WithCategoryFilter`:

```csharp
await BenchmarkHost.Create(args)
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

`WithCategoryFilter` composes with CLI flags: each include source must match independently, while exclude lists are unioned. This lets you set a default include list in code and still narrow it from the command line.

## Categories in reports

- **JSON** always emits a `categories` array on every `BenchmarkResult`.
- **Markdown**, **CSV**, and **Console** reporters show a `Categories` column in **advanced** detail only.
- `--list` prints categories next to each benchmark when any are present.

```bash
dotnet run -- --list
dotnet run -- --reporter markdown --detail advanced --output ./results
```
