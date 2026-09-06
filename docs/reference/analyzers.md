---
title: Analyzers
description: Compile-time diagnostics that catch common NBenchmark configuration errors before you run your benchmarks.
order: 2
---

# Analyzers

NBenchmark ships a set of Roslyn diagnostic analyzers that detect configuration issues at edit time. You get live warnings and errors in your IDE and during `dotnet build`.

## Setup

There is none. The analyzers and their code fixes arrive inside the `NBenchmark` package and run without configuration:

```bash
dotnet add package NBenchmark
```

They ship this way on purpose. The mistakes these rules catch - a capturing body, a body with no observable side effect, a body the JIT can delete - all produce a number that looks perfectly reasonable, so an author who makes one has no reason to suspect anything is wrong. An analyzer nobody installed would never have told them.

If you used the separate `NBenchmark.Analyzers` package, remove the `PackageReference`. The rule IDs, severities, and code fixes are unchanged.

## Diagnostic reference

| ID | Title | Severity | Description |
| --- | --- | --- | --- |
| NB0001 | Benchmark class must have a public parameterless constructor | Warning | A class or record with `[Benchmark]` methods lacks a public parameterless constructor. |
| NB0002 | `[Benchmark]` method must not be static | Error | A method marked `[Benchmark]` is `static`. Only instance methods are discovered. |
| NB0003 | `[Arguments]` / `[ArgumentsSource]` must match method parameters | Error | The number of `[Arguments]` values does not match the method's parameter count. |
| NB0004 | `[Benchmark]` body has no observable side effects | Error | A void `[Benchmark]` method body has no observable side effects, allowing the JIT to eliminate it. |
| NB0005 | `[Benchmark]` body does no observable work | Error | A void `[Benchmark]` method has an empty body. |
| NB0006 | Multiple `[Benchmark(Baseline = true)]` methods in the same class | Error | Only one benchmark per class can be the baseline. |
| NB0007 | Duplicate lifecycle method in benchmark class | Error | Two methods share the same lifecycle attribute (e.g., `[GlobalSetup]`). |
| NB0008 | `[Benchmark]` property value out of range | Error | `Samples` or `WarmupSamples` on `[Benchmark]` is outside the valid range. |
| NB0009 | `MeasurementOptions` property value out of range | Error | A property in a `MeasurementOptions` initializer is outside the valid range. |
| NB0010 | Benchmark body is throwaway | Warning | A lambda passed to a `Benchmark.Run*` `Action` overload has no observable side effects. |
| NB0011 | `PerClass` lifetime with scoped service may contaminate state | Warning | A class uses `PerClass` lifetime and injects a constructor dependency that may hold per-instance state (any non-primitive, non-ambient reference type), which can leak warmed state across benchmark methods. |
| NB0012 | `[ArgumentsSource]` cannot be combined with `[Arguments]` | Error | A method uses both attributes, which is ambiguous. |
| NB0013 | `PerClass` lifetime with mutable instance field may contaminate state | Warning | A class uses `PerClass` lifetime and has a mutable instance field accessed by multiple benchmarks. |
| NB0014 | Benchmark body captures state | Info | A lambda passed to `Benchmark.Run*` or `BenchmarkSuite.Add` captures local state. |

### NB0001 - Missing parameterless constructor

This diagnostic applies to any class or record that contains declared `[Benchmark]` methods but lacks a public parameterless constructor. NBenchmark uses `Activator.CreateInstance` by default, which requires this constructor. Structs are not flagged because their implicit zero-init constructor satisfies the discovery pipeline.

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

To fix this, you can:
1. Add a public parameterless constructor.
2. Use `NBenchmark.DependencyInjection` to resolve from a DI container.

### NB0002 - Static benchmark method

The `[Benchmark]` discovery pipeline only looks for instance methods. NBenchmark silently skips static methods.

```csharp
// Bad
[Benchmark]
public static void Measure() { }

// Good
[Benchmark]
public void Measure() { }
```

This diagnostic includes an automatic code fix that removes the `static` keyword.

### NB0003 - Arguments arity mismatch

The `[Arguments]` attribute must match the method's parameter count. Each attribute corresponds to one invocation of the method. When using `[ArgumentsSource]`, the source method must yield tuples whose arity matches the benchmark method's parameter count.

```csharp
// Bad - method takes no parameters but has [Arguments]
[Arguments(42)]
[Benchmark]
public void Measure() { }

// Bad - method expects one parameter, argument supplies none
[Arguments]
[Benchmark]
public void Measure(int x) { }

// Bad - [ArgumentsSource] source yields tuple with wrong arity
[ArgumentsSource(nameof(Cases))]
[Benchmark]
public void Measure(int x, int y) { }

public static IEnumerable<(int a,)> Cases() { yield return (1,); } // arity 1, expected 2
```

### NB0004 / NB0005 - No observable side effects

If a `[Benchmark]` method body contains only pure operations (such as local variable assignments, empty loops, or no method calls), the JIT may optimize the entire body away. This results in 0 ns measurements.

NBenchmark uses a syntax-level heuristic to detect bodies with no observable side effects. A body is flagged if it lacks:
- Method calls
- Field or property writes
- `ref` or `out` arguments
- `return` statements with values
- `await` expressions
- Object or array allocations

These diagnostics have `Error` severity in harness mode because a benchmark with no observable work is an invalid definition. The build fails so you can catch the problem in CI/CD before the suite runs.

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

If the work happens outside the method syntax (for example, via native interop or external state mutation), suppress the diagnostic locally:

```csharp
#pragma warning disable NBenchmark.NB0004 // P/Invoke call mutates native state
[Benchmark]
public void NativeBuffer()
{
    NativeMethods.FillBuffer(_buffer);
}
#pragma warning restore NBenchmark.NB0004
```

You can also lower the severity project-wide in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = warning
dotnet_diagnostic.NB0005.severity = warning
```

### NB0006 - Multiple baselines

Only one benchmark per class can be the baseline. When multiple methods have `Baseline = true`, NBenchmark uses the first discovered method and ignores the others.

```csharp
// Bad
[Benchmark(Baseline = true)] public void MethodA() { }
[Benchmark(Baseline = true)] public void MethodB() { }
```

### NB0007 - Duplicate lifecycle methods

Each lifecycle attribute (`[GlobalSetup]`, `[GlobalTeardown]`, `[SampleSetup]`, `[SampleTeardown]`) should appear at most once per class. If two methods share the same attribute, NBenchmark silently ignores the second one.

```csharp
// Bad - duplicate [GlobalSetup]
[GlobalSetup] public void Init() { }
[GlobalSetup] public void InitAgain() { }
```

### NB0008 / NB0009 - range violations

NBenchmark checks `[Benchmark]` attribute properties and `MeasurementOptions` values against their valid ranges at compile time to prevent `ArgumentOutOfRangeException` at runtime.

```csharp
// Bad - Samples exceeds MaxSamplesLimit (100,000)
[Benchmark(Samples = 200000)]
public void Measure() { }

// Bad - ConfidenceLevel must be strictly between 0 and 1
var opts = new MeasurementOptions { ConfidenceLevel = 1.5 };

// Bad - 'with' expression is also checked
var opts2 = new MeasurementOptions() with { Samples = 200000 };
```

### NB0010 - Throwaway lambda body

When a lambda passed to an `Action` overload of `Benchmark.Run*` has no observable side effects, the JIT may eliminate it. An empty lambda or one that only assigns to a local variable has no observable effect on the program state.

NB0010 is a `Warning` because Single mode is intended for ad-hoc exploration. Warnings do not break the build, allowing you to iterate.

```csharp
// Warning - empty lambda, nothing to measure
Benchmark.Run(() => { });

// Warning - assigns to a local; local is discarded
Benchmark.Run(() => { var x = 42; });

// No warning - has observable side effects
Benchmark.Run(() => { _x = 42; });
Benchmark.Run(() => Compute());
```

Value-returning overloads (such as `Benchmark.Run<T>`) are not flagged because NBenchmark consumes the returned value, which prevents dead-code elimination.

### NB0011 - `PerClass` lifetime with scoped service

When a class uses `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in that class share one object instance. If the class constructor injects a dependency that may hold per-instance state, one method can warm caches that the next method reads, distorting the timing.

The analyzer flags any non-primitive, non-ambient reference-type constructor parameter on a class with two or more `[Benchmark]` methods. Well-known stateless types (such as `ILogger` and `IOptions<T>`) and ambient types (such as `HttpContext`) are excluded. Non-public constructors are also inspected.

```csharp
// Warning NB0011
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class OrderBenchmarks(MyDbContext db)
{
    [Benchmark] public int A() => db.Orders.Count();
    [Benchmark] public int B() => db.Orders.Where(o => o.Total > 100).Count();
}
```

The fluent default also triggers this warning. If `BenchmarkHarness.WithInstanceLifetime(InstanceLifetime.PerClass)` is called, every discovered class in the assembly is treated as `PerClass`.

An empty `IStateReset` is also flagged:

```csharp
// Still warning NB0011: resets nothing
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class OrderBenchmarks(MyDbContext db) : IStateReset
{
    public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;
    ...
}
```

The engine can only check for the presence of the interface; the analyzer reads the method body to ensure the reset is not empty. If the carry-over is deliberate, use `[SharedState]`.

**Why this matters**: The Mann-Whitney U test assumes samples are independent. When method A warms a shared cache that method B reads, method B's timings are artificially linked to method A running first. This breaks the statistical model and makes the significance verdict unreliable.

To fix this, you can:
1. Remove the attribute to use `PerMethod` lifetime.
2. Implement `IStateReset` to reset shared state between methods.
3. Add `[SharedState]` if the carry-over is intentional.
4. Suppress the warning with `#pragma warning disable NB0011`.

> [!NOTE]
> This is a compile-time warning. If you suppress NB0011, verify that shared state does not create timing dependencies by running each method in isolation and comparing results.

### NB0012 - `[ArgumentsSource]` cannot be combined with `[Arguments]`

A method cannot carry both attributes. `[Arguments]` declares literal cases inline, while `[ArgumentsSource]` names a programmatic source. Combining them is ambiguous and results in an error.

```csharp
// Error NB0012: both attributes on one method
[Arguments(10)]
[ArgumentsSource(nameof(SortCases))]
[Benchmark]
public void Sort(int size) { }

static IEnumerable<(int Size, string Label)> SortCases() => ...
```

Use one or the other. For small literal lists, use `[Arguments]`. For generated values or parameter sweeps, use `[ArgumentsSource]`. See [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) for a full comparison.

### NB0013 - `PerClass` lifetime with mutable instance field

When a class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and has a non-`readonly` instance field accessed by at least two `[Benchmark]` methods, the field can carry warmed state between methods. This violates the statistical-independence assumption.

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

To fix this:
1. Remove the attribute to use `PerMethod` lifetime.
2. Make the field `readonly` if it is only assigned once.
3. Suppress the warning with `#pragma warning disable NB0013` if sharing state is intentional.

### NB0014 - Capturing body cannot be isolated

NBenchmark measures benchmark bodies in a separate worker process. It achieves this by resolving the method the compiler emitted. Because a lambda that captures state cannot be addressed this way, NBenchmark sends the captured *values* instead.

Most ordinary data is sent faithfully and the benchmark remains isolated. However, a value whose behavior is not determined by its contents (such as a `Stream` or a collection with a custom comparer) is refused, and the run fails.

```csharp
var data = BuildInput();

// Info NB0014: captures 'data'
Benchmark.Run(() => Process(data));
```

NB0014 is an `Info` diagnostic, not a warning, because capturing is the idiomatic way to benchmark over prepared data.

| Body | Isolated? | Why |
| --- | --- | --- |
| `() => 43` | yes | Nothing to carry. |
| `static () => 43` | yes | Identical to above. |
| `() => Work(local)` | if `local` is faithful | Types like `int`, `string`, or `int[]` are sent by value. `Stream` is not. |
| `() => Work(_field)` | if `this` is faithful | Captures `this`; every field must qualify. |
| `() => Work(StaticField)` | yes | Static fields need no receiver. |
| `widget.Compute` | if `widget` is faithful | The receiver is walked field by field. |
| `() => Work(shapes)` | no | Collections are sent against their element type; overrides are lost. |
| `() => Work(local)` in `Method<T>` | if `local` is faithful | Closure is rebuilt against the same type arguments. |
| `() => 43` beside `() => local` | yes | Non-capturing lambdas remain isolated. |

If a body is part of a `BenchmarkSuite`, it is measured by a worker. If one body cannot be isolated, the entire suite falls back to the host process.

```csharp
var data = BuildInput();

await new BenchmarkSuite("Sorting")
    // Info NB0014: captures 'data' - the whole suite falls back to this process
    .Add("Sort", () => Array.Sort(data))
    .Add("Own", () => { var own = BuildInput(); Array.Sort(own); })
    .RunAsync();
```

To isolate the body, move the state creation inside the lambda:

```csharp
// No capture: the body builds what it needs
Benchmark.Run(() => Process(BuildInput()));
```

Alternatively, use a `[Benchmark]` class. Discovery runs inside the worker, so `[GlobalSetup]` and fields are built locally:

```csharp
public class ProcessBenchmarks
{
    private Input _data = null!;

    [GlobalSetup] public void Setup() => _data = BuildInput();

    [Benchmark] public Output Run() => Process(_data);
}
```

## Runtime independence warning

In addition to compile-time analyzers, NBenchmark emits a runtime warning on `BenchmarkResult.Warnings` when a class runs under `InstanceLifetime.PerClass` with multiple `[Benchmark]` methods but declares neither `IStateReset` nor `[SharedState]`. This covers cases where the analyzer is not installed or in suite mode.

To opt out, declare `[SharedState]` on the class:

```csharp
[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState] // measuring the warm-cache path is the point
public class CacheBenchmarks { }
```

You can also suppress this warning for the entire run by setting `SuppressedWarnings = BenchmarkWarnings.PerClassIndependence` on `MeasurementOptions`.

## Disabling a rule

Use a `#pragma` directive to suppress a specific diagnostic. Always include a comment explaining why:

```csharp
#pragma warning disable NBenchmark.NB0004 // P/Invoke mutates native state
[Benchmark]
public void Measure()
{
    NativeMethods.FillBuffer(_buffer);
}
#pragma warning restore NBenchmark.NB0004
```

Alternatively, set the severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = none
```

## Severity

- **Errors**: The benchmark cannot run or will produce meaningless results. (NB0002, NB0003, NB0004, NB0005, NB0006, NB0007, NB0008, NB0009).
- **Warnings**: The code can run, but measurements may be invalid. (NB0001, NB0010, NB0011, NB0013).
- **Info**: The measurement is fine, but the mechanism is worth noting. (NB0014).

You can override any severity in `.editorconfig`.
