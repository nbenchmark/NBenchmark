---
title: Analyzers
description: Compile-time diagnostics that catch common NBenchmark configuration errors before you run your benchmarks.
order: 2
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
| NB0001 | Benchmark class must have a public parameterless constructor | Warning | A class or record with `[Benchmark]` methods has no public parameterless constructor. Add one, or use `NBenchmark.DependencyInjection`. |
| NB0002 | `[Benchmark]` method must not be static | Error | A method is marked `[Benchmark]` but is `static`. Only instance methods are discovered. Remove the `static` keyword. |
| NB0003 | `[BenchmarkCase]` / `[BenchmarkCases]` must match method parameters | Error | The number of `[BenchmarkCase]` values does not match the method's parameter count, or the `[BenchmarkCases]` source yields a tuple arity that does not match. Also covers missing or non-existent source methods. |
| NB0004 | `[Benchmark]` body has no observable side effects | Error | A void `[Benchmark]` method body has no observable side effects. The JIT may eliminate it, producing 0 ns results. |
| NB0005 | `[Benchmark]` body does no observable work | Error | A void `[Benchmark]` method has an empty body (no statements at all). The JIT will eliminate it. |
| NB0006 | Multiple `[Benchmark(Baseline = true)]` methods in the same class | Error | Only one benchmark per class can have `Baseline = true`. Remove the attribute from all but one. |
| NB0007 | Duplicate lifecycle method in benchmark class | Error | Two methods in the same class share the same lifecycle attribute (`[BenchmarkSetup]`, `[BenchmarkTeardown]`, `[BenchmarkIterationSetup]`, `[BenchmarkIterationTeardown]`). Remove the duplicate. |
| NB0008 | `[Benchmark]` property value out of range | Error | `Iterations` or `WarmupIterations` on `[Benchmark]` is outside the valid range (0-100000 for iterations, 0-10000 for warmup, or -1 for the default). |
| NB0009 | `MeasurementOptions` property value out of range | Error | `Iterations`, `WarmupIterations`, or `ConfidenceLevel` in a `MeasurementOptions` object initializer or `with` expression is outside the valid range. |
| NB0010 | Benchmark body is throwaway | Warning | A lambda passed to the `Action` overloads of `Benchmark.Run()`, `Benchmark.RunAsync()`, `Benchmark.RunRaw()`, or `Benchmark.RunRawAsync()` has no observable side effects. The JIT may eliminate it, producing 0 ns results. |
| NB0011 | `PerClass` lifetime with scoped service may contaminate state | Warning | A benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and injects a constructor dependency that may hold per-instance state (any non-primitive, non-ambient reference type), which can leak warmed state across benchmark methods. |
| NB0012 | `[BenchmarkCases]` cannot be combined with `[BenchmarkCase]` | Error | A method has both `[BenchmarkCase]` and `[BenchmarkCases]`. Use one or the other. |
| NB0013 | `PerClass` lifetime with mutable instance field may contaminate state | Warning | A benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and has a mutable instance field that is read or written by at least two `[Benchmark]` methods, which can leak warmed state across methods. |

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
2. Use `NBenchmark.DependencyInjection` to resolve from a DI container

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

### NB0003 - BenchmarkCase arity mismatch

The `[BenchmarkCase]` attribute must match the method's parameter count. Each attribute corresponds to one invocation of the method. When using `[BenchmarkCases]`, the source method must yield tuples whose arity matches the benchmark method's parameter count.

```csharp
// Bad - method takes no parameters but has [BenchmarkCase]
[BenchmarkCase(42)]
[Benchmark]
public void Measure() { }

// Bad - method expects one parameter, argument supplies none
[BenchmarkCase]
[Benchmark]
public void Measure(int x) { }

// Bad - [BenchmarkCases] source yields tuple with wrong arity
[BenchmarkCases(nameof(Cases))]
[Benchmark]
public void Measure(int x, int y) { }

public static IEnumerable<(int a,)> Cases() { yield return (1,); } // arity 1, expected 2
```

### NB0004 / NB0005 - No observable side effects

If a `[Benchmark]` method body contains only pure operations (local variable assignments, empty loops, no method calls, no field writes, no return value), the JIT may optimise the entire body away, producing a result of 0 ns. A syntax-level heuristic detects when a body has no observable side effects:

- No method calls
- No field/property writes
- No `ref`/`out` arguments
- No `return` statements with values
- No `await` expressions
- No object or array allocations

These diagnostics are `Error` severity in host mode because a benchmark with no observable work is not a measurement issue - it is an invalid benchmark definition. The build fails so the problem is caught in CI/CD before the suite runs.

```csharp
// Bad - build fails with NB0005
[Benchmark]
public void Empty() { }

// Bad - build fails with NB0004
[Benchmark]
public void PureLoop() { for (var i = 0; i < 1000; i++) { } }

// Good - side effect through a consumed return value
[Benchmark]
public int Measure() { return Compute(); }

// Good - observable side effect
[Benchmark]
public void Mutate() { _counter++; }
```

When the analyzer cannot see the work because it happens outside the method syntax (for example native interop, external state mutation, or calls the analyzer does not recognize), suppress the diagnostic locally and document why:

```csharp
#pragma warning disable NBenchmark.NB0004 // P/Invoke call mutates native state
[Benchmark]
public void NativeBuffer()
{
    NativeMethods.FillBuffer(_buffer);
}
#pragma warning restore NBenchmark.NB0004
```

You can also lower the severity project-wide in `.editorconfig` if your codebase frequently encounters false positives:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = warning
dotnet_diagnostic.NB0005.severity = warning
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

When a lambda expression passed to an `Action` overload of `Benchmark.Run()`, `Benchmark.RunAsync()`, `Benchmark.RunRaw()`, or `Benchmark.RunRawAsync()` has no observable side effects, the JIT may eliminate it. An empty lambda or one that only assigns to a local variable has no observable effect on the program state.

NB0010 is a `Warning` because quick mode is intended for ad-hoc exploration. Warnings do not break the build, so you can start with a simple lambda and iterate.

```csharp
// Warning - empty lambda, nothing to measure
Benchmark.Run(() => { });

// Warning - assigns to a local; local is discarded
Benchmark.Run(() => { var x = 42; });

// No warning - has observable side effects (field write, method call, etc.)
Benchmark.Run(() => { _x = 42; });
Benchmark.Run(() => Compute());  // method call
```

Value-returning overloads such as `Benchmark.Run<T>`, `Benchmark.RunAsync<T>`, `Benchmark.RunRaw<T>`, and `Benchmark.RunRawAsync<T>` are not flagged because NBenchmark consumes the returned value internally, which prevents dead-code elimination.

### NB0011 - `PerClass` lifetime with scoped service

When a class uses `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in that class share one object instance. If the class constructor takes a dependency that may hold per-instance state, one method can warm caches that the next method reads, which distorts timing.

The analyzer flags any non-primitive, non-ambient reference-type constructor parameter. Well-known stateless types (`ILogger<T>`, `IOptions<T>`) and ambient types (`HttpContext`, `IServiceProvider`, `CancellationToken`) are excluded.

```csharp
// Warning NB0011
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class OrderBenchmarks(MyDbContext db)
{
    [Benchmark] public int A() => db.Orders.Count();
    [Benchmark] public int B() => db.Orders.Where(o => o.Total > 100).Count();
}
```

**Why this matters.** The Mann-Whitney U test used for significance assumes samples are independent. When method A warms a shared cache that method B reads, method B's timings are artificially linked to method A running first. The shuffling math breaks and the significance verdict becomes unreliable. This is not a measurement-quality concern - it is a correctness concern for the statistical model.

Typical fixes:

1. Remove the attribute so the class uses `PerMethod`
2. Keep `PerClass` and suppress with `#pragma warning disable NB0011` when sharing state is intentional

> **CI note.** This is a compile-time warning, not a runtime error. In CI/CD pipelines the warning scrolls past in the build log and is easy to miss. If you suppress NB0011, verify that the shared state does not create a timing dependency between methods - for example, by running each method in isolation and comparing results.

### NB0013 - `PerClass` lifetime with mutable instance field

When a class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and has a non-`readonly` instance field that is accessed by at least two `[Benchmark]` methods, the field can carry warmed state from one method to the next, violating the statistical-independence assumption.

```csharp
// Warning NB0013
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class CacheBenchmarks
{
    private int _counter;

    [Benchmark] public int A() => _counter++;
    [Benchmark] public int B() => _counter++;
}
```

Typical fixes:

1. Remove the attribute so the class uses `PerMethod`
2. Make the field `readonly` if it is only assigned once
3. Keep `PerClass` and suppress with `#pragma warning disable NB0013` when sharing state is intentional

## Runtime independence warning

In addition to the compile-time analyzers above, NBenchmark emits a runtime warning on every `BenchmarkResult.Warnings` list when a class uses `InstanceLifetime.PerClass` and has more than one `[Benchmark]` method. This covers suite mode (where analyzers do not run) and cases where the analyzer package is not installed.

The runtime warning is opt-out: set `SuppressPerClassIndependenceWarning` to `true` on `MeasurementOptions` to silence it when sharing is intentional.

```csharp
// Suppress the runtime warning
var host = BenchmarkHost.Create(args)
    .WithOptions(new MeasurementOptions { SuppressPerClassIndependenceWarning = true });
```

## Disabling a rule

Use a `#pragma` directive to suppress a specific diagnostic. Always add a comment explaining why the suppression is legitimate:

```csharp
#pragma warning disable NB0004 // P/Invoke mutates native state that the analyzer cannot see
[Benchmark]
public void Measure()
{
    NativeMethods.FillBuffer(_buffer);
}
#pragma warning restore NB0004
```

Or set the severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = none
```

## Severity

Diagnostics use the default severity listed in the table above. The default is chosen by where the problem sits on the invalid-to-suspicious spectrum:

- **Errors** mean the benchmark cannot run or will produce meaningless results. NB0002, NB0003, NB0004, NB0005, NB0006, NB0007, NB0008, and NB0009 are errors.
- **Warnings** mean the code can run but the measurements may be invalid. NB0001, NB0010, NB0011, and NB0013 are warnings.

You can override the severity of any diagnostic in `.editorconfig`. For example, to make all throwaway-lambda warnings errors in quick mode too:

```ini
[*.cs]
dotnet_diagnostic.NB0010.severity = error
```

Or to downgrade host-mode body-effect errors to warnings in a legacy codebase while you migrate:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = warning
dotnet_diagnostic.NB0005.severity = warning
```
