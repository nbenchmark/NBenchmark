# Contributing to NBenchmark

Thanks for your interest in contributing! This guide covers the basics.

## Prerequisites

- .NET 8.0, 9.0, and 10.0 SDKs installed
- A Git client

## Build and Test

```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

The library projects multi-target `net8.0;net9.0;net10.0`. Tests run against all three TFMs by default.

## Project Structure

```
src/NBenchmark/                        Core library (zero dependencies)
src/NBenchmark.Reporters.Console/     Spectre.Console terminal output
src/NBenchmark.DependencyInjection/     Microsoft.Extensions.DependencyInjection integration
tests/NBenchmark.Tests/                 Core library tests
tests/NBenchmark.Reporters.Console.Tests/  Console reporter tests
tests/NBenchmark.DependencyInjection.Tests/  DI integration tests
samples/                                Runnable example projects
docs/                                   Documentation site
```

## Coding Conventions

- File-scoped namespaces
- 4-space indentation
- `var` for obvious types
- XML doc comments (`///`) on public APIs
- `internal` for implementation details not part of the public API
- No `#region` directives
- No commented-out code

## Pull Requests

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Ensure all tests pass (`dotnet test --configuration Release`)
5. Submit the PR with a clear description of the change and motivation

### PR Guidelines

- One logical change per PR
- Include tests for bug fixes and new features
- Keep public API changes minimal and documented. A change to a shipping library's public surface fails the build until you add it to that project's `PublicAPI.Unshipped.txt`, and a new or re-severity-ed analyzer rule fails until you add a row to `src/NBenchmark.Analyzers/AnalyzerReleases.Unshipped.md` - both diffs are part of the review.
- Follow the existing code style - the repo uses an `.editorconfig`

## Reporting Issues

When filing an issue, please include:

- .NET version and OS
- Minimal reproduction steps
- Expected vs. actual behavior

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
