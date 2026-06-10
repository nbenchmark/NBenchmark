---
title: Analyzers
description: Compile-time diagnostics that catch common NBenchmark configuration errors before you run your benchmarks.
order: 7
---

# Analyzers

NBenchmark.Analyzers ships a set of Roslyn diagnostic analyzers that detect configuration issues at edit time. Install the package to get live warnings and errors in your IDE and during `dotnet build`.

## Installation

```bash
dotnet add package NBenchmark.Analyzers
```

The analyzers run automatically. No additional configuration is needed. The package ships both analyzers (diagnostics) and code fixes (automatic corrections).

## Diagnostic reference

| ID | Title | Severity | Description |
|---|---|---|---|
| NB0001 | Benchmark class must have a public parameterless constructor | Warning | A class or record with `[Benchmark]` methods has no public parameterless constructor. Add one, or use `NBenchmark.Extensions.DependencyInjection`. |
| NB0002 | `[Benchmark]` method must not be static | Error | A method is marked `[Benchmark]` but is `static`. Only instance methods are discovered. Remove the `static` keyword. |
| NB0003 | `[BenchmarkArguments]` must match method parameters | Error | The number of `[BenchmarkArguments]` values does not match the method's parameter count, or `[BenchmarkArguments]` is present when the method has no parameters. |
| NB0004 | `[Benchmark]` body has no observable side effects | Info | A void `[Benchmark]` method body has no observable side effects. The JIT may eliminate it, producing 0 ns results. |
| NB0005 | `[Benchmark]` body does no observable work | Warning | A void `[Benchmark]` method has an empty body (no statements at all). The JIT will eliminate it. |
| NB0006 | Multiple `[Benchmark(Baseline = true)]` methods in the same class | Error | Only one benchmark per class can have `Baseline = true`. Remove the attribute from all but one. |
| NB0007 | Duplicate lifecycle method in benchmark class | Error | Two methods in the same class share the same lifecycle attribute (`[BenchmarkSetup]`, `[BenchmarkTeardown]`, `[BenchmarkIterationSetup]`, `[BenchmarkIterationTeardown]`). Remove the duplicate. |
| NB0008 | `[Benchmark]` property value out of range | Error | `Iterations` or `WarmupIterations` on `[Benchmark]` is outside the valid range (0-100000 for iterations, 0-10000 for warmup, or -1 for the default). |
| NB0009 | `MeasurementOptions` property value out of range | Error | `Iterations`, `WarmupIterations`, or `ConfidenceLevel` in a `MeasurementOptions` object initializer or `with` expression is outside the valid range. |
| NB0010 | Benchmark body is throwaway | Warning | A lambda passed to the `Action` overloads of `Benchmark.Run()` or `Benchmark.RunRaw()` has no observable side effects. The JIT may eliminate it, producing 0 ns results. |

### NB0001 - Missing parameterless constructor

Applies to any class or record that contains declared `[Benchmark]` methods (inherited methods do not count - they are not discovered) but has no public parameterless constructor. NBenchmark uses `Activator.CreateInstance` by default, which requires a public parameterless constructor. Structs are not flagged because the implicit zero-init constructor satisfies the discovery pipeline.

```csharp
// Bad - no public parameterless constructor
public class MyBenchmarks
{
    private readonly IDependency _dep;

    public MyBenchmarks(IDependency dep) { _dep = dep; }

    [Benchmark]
    public void Measure() { }
}
```

Fix options:

1. Add a public parameterless constructor
2. Use `NBenchmark.Extensions.DependencyInjection` to resolve from a DI container

### NB0002 - Static benchmark method

The `[Benchmark]` discovery pipeline only looks for instance methods. Static methods are silently skipped.

```csharp
// Bad
[Benchmark]
public static void Measure() { }

// Good
[Benchmark]
public void Measure() { }
```

This diagnostic has an automatic code fix that removes the `static` keyword.

### NB0003 - BenchmarkArguments arity mismatch

The [BenchmarkArguments] attribute must match the method's parameter count. Each attribute corresponds to one invocation of the method.

```csharp
// Bad - method takes no parameters
[BenchmarkArguments(42)]
[Benchmark]
public void Measure() { }

// Bad - method expects one parameter, argument supplies none
[BenchmarkArguments]
[Benchmark]
public void Measure(int x) { }
```

### NB0004 / NB0005 - No observable side effects

If a `[Benchmark]` method body contains only pure operations (local variable assignments, empty loops, no method calls, no field writes, no return value), the JIT may optimise the entire body away, producing a result of 0 ns. A syntax-level heuristic detects when a body has no observable side effects:

- No method calls
- No field/property writes
- No `ref`/`out` arguments
- No `return` statements with values
- No `await` expressions
- No object or array allocations

```csharp
// Bad - JIT will eliminate this
[Benchmark]
public void Measure() { for (var i = 0; i < 1000; i++) { } }

// Good - side effect through a consumed return value
[Benchmark]
public int Measure() { return Compute(); }
```

### NB0006 - Multiple baselines

Only one benchmark per class can be the baseline. When multiple methods have `Baseline = true`, only the first one discovered is used and the others are ignored.

```csharp
// Bad
[Benchmark(Baseline = true)] public void MethodA() { }
[Benchmark(Baseline = true)] public void MethodB() { }
```

### NB0007 - Duplicate lifecycle methods

Each lifecycle attribute (`[BenchmarkSetup]`, `[BenchmarkTeardown]`, `[BenchmarkIterationSetup]`, `[BenchmarkIterationTeardown]`) should appear at most once per class. If two methods share the same attribute, the second one is silently ignored.

```csharp
// Bad - duplicate [BenchmarkSetup]
[BenchmarkSetup] public void Init() { }
[BenchmarkSetup] public void InitAgain() { }
```

### NB0008 / NB0009 - Range violations

`[Benchmark]` attribute properties and `MeasurementOptions` object initializer values are checked against their valid ranges at compile time rather than waiting for an `ArgumentOutOfRangeException` at runtime.

```csharp
// Bad - Iterations exceeds MaxIterations (100000)
[Benchmark(Iterations = 200000)]
public void Measure() { }

// Bad - ConfidenceLevel must be strictly between 0 and 1
var opts = new MeasurementOptions { ConfidenceLevel = 1.5 };

// Bad - 'with' expression is also checked
var opts2 = new MeasurementOptions() with { Iterations = 200000 };
```

### NB0010 - Throwaway lambda body

When a lambda expression passed to the `Action` overloads of `Benchmark.Run()` or `Benchmark.RunRaw()` has no observable side effects, the JIT may eliminate it. An empty lambda or one that only assigns to a local variable has no observable effect on the program state.

```csharp
// Bad - empty lambda, nothing to measure
Benchmark.Run(() => { });

// Bad - assigns to a local; local is discarded
Benchmark.Run(() => { var x = 42; });

// Good - has observable side effects (field write, method call, etc.)
Benchmark.Run(() => { _x = 42; });
Benchmark.Run(() => Compute());  // method call
```

Note: NB0010 inspects only overloads whose first parameter is an `Action` (void delegate). `Benchmark.Run<T>`, `Benchmark.RunAsync`, `Benchmark.RunAsync<T>`, and `RunRaw` overloads that accept value-returning delegates are not flagged.

## Disabling a rule

Use a `#pragma` directive to suppress a specific diagnostic:

```csharp
#pragma warning disable NB0004
[Benchmark]
public void Measure()
{
    for (var i = 0; i < 1000; i++) { }
}
#pragma warning restore NB0004
```

Or set the severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = none
```

## Severity

Most diagnostics have the default severity listed in the table above. The NB0002 and NB0003 diagnostics are `Error` because they prevent discovery entirely. NB0006, NB0007, NB0008, and NB0009 are `Error` because they represent definite configuration mistakes. NB0001 and NB0010 are `Warning` because the code will still run (the issue is caught later at runtime). NB0004 is `Info` because the heuristic is conservative and may have false positives.
