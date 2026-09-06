# Changelog

NBenchmark follows
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Every package below is versioned and released together, so a version number means the same thing
across all of them.

| Package | What it adds |
| --- | --- |
| [`NBenchmark`](https://www.nuget.org/packages/NBenchmark) | The engine, the harness, the CLI, the measurement worker, and the build-time analyzers |
| [`NBenchmark.Reporters.Console`](https://www.nuget.org/packages/NBenchmark.Reporters.Console) | Rich terminal tables via Spectre.Console |
| [`NBenchmark.Exporters.OpenTelemetry`](https://www.nuget.org/packages/NBenchmark.Exporters.OpenTelemetry) | Streams per-sample telemetry over OTLP |
| [`NBenchmark.DependencyInjection`](https://www.nuget.org/packages/NBenchmark.DependencyInjection) | Constructor injection for benchmark classes |
| [`NBenchmark.Integration.xUnit`](https://www.nuget.org/packages/NBenchmark.Integration.xUnit) | Performance gates as xUnit tests |
| [`NBenchmark.Integration.NUnit`](https://www.nuget.org/packages/NBenchmark.Integration.NUnit) | Performance gates as NUnit tests |
| [`NBenchmark.Integration.MSTest`](https://www.nuget.org/packages/NBenchmark.Integration.MSTest) | Performance gates as MSTest tests |
| [`NBenchmark.Integration.Abstractions`](https://www.nuget.org/packages/NBenchmark.Integration.Abstractions) | The shared contract the three test integrations build on |
| [`NBenchmark.Tool`](https://www.nuget.org/packages/NBenchmark.Tool) | `dotnet benchmark`, for running benchmarks in any assembly without a host project |

## Releases

Release notes for tagged versions are on the
[GitHub Releases page](https://github.com/nbenchmark/NBenchmark/releases).
