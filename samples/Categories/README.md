# Categories sample

This sample shows how to tag benchmarks with `[BenchmarkCategory]` and then filter runs by category from the command line.

## Run everything

```bash
dotnet run
```

## Run only the fast string benchmarks

```bash
dotnet run -- --category Fast
```

## Run all string benchmarks except the slow one

```bash
dotnet run -- --category String --exclude-category Slow
```

## Combine category filtering with a glob filter

```bash
dotnet run -- --category String --filter CategorizedBenchmarks.Con*
```

## Show categories in the list output

```bash
dotnet run -- --list
```

## Save an advanced Markdown report that includes categories

```bash
dotnet run -- --reporter markdown --detail advanced --output ./results
```

The Markdown and CSV reporters show the `Categories` column only in advanced detail. JSON always includes the `categories` array.
