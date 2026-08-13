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
| --- | --- | --- | --- |
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
| NB0014 | Benchmark body captures state | Info | A lambda passed to `Benchmark.Run()`, `Benchmark.RunAsync()`, `Benchmark.RunRaw()`, `Benchmark.RunRawAsync()` or `BenchmarkSuite.Add()` captures a local, a parameter, or `this`. Ordinary data is sent to the worker and the body is still isolated; a value whose behaviour is not determined by its contents is refused, which fails the run. `IsolationStatus` on the result is the authority. |
| NB0015 | Conflicting isolation attributes | Error | One member carries both `[InProcess]` and `[IsolatedProcess]`. The two ask for opposite things. Remove one. |

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

These diagnostics are `Error` severity in harness mode because a benchmark with no observable work is not a measurement issue - it is an invalid benchmark definition. The build fails so the problem is caught in CI/CD before the suite runs.

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

NB0010 is a `Warning` because Single mode is intended for ad-hoc exploration. Warnings do not break the build, so you can start with a simple lambda and iterate.

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

The analyzer flags any non-primitive, non-ambient reference-type constructor parameter, on a class with **two or more** `[Benchmark]` methods (one method cannot contaminate itself). Well-known stateless types (`ILogger`, `ILogger<T>`, `ILoggerFactory`, `IOptions<T>`) and ambient types (`HttpContext`, `IServiceProvider`, `CancellationToken`) are excluded. Non-public constructors are inspected, because a container resolves one perfectly well.

```csharp
// Warning NB0011
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class OrderBenchmarks(MyDbContext db)
{
    [Benchmark] public int A() => db.Orders.Count();
    [Benchmark] public int B() => db.Orders.Where(o => o.Total > 100).Count();
}
```

**The fluent default counts too.** `BenchmarkHarness.WithInstanceLifetime(InstanceLifetime.PerClass)` makes every discovered class in the assembly PerClass, and produces exactly the same sharing as the attribute. A class carrying no `[InstanceLifetime]` attribute is therefore reported when that call appears anywhere in the same compilation - reported at the end of the compilation, because the call is usually in a different file from the class it decides for. A harness in a *separate project* is out of reach: no analyzer can see it.

**An empty `IStateReset` is reported, not trusted.**

```csharp
// Still warning NB0011: resets nothing
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class OrderBenchmarks(MyDbContext db) : IStateReset
{
    public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;
    ...
}
```

The engine can only check that the interface is present, so an empty body silences the runtime safeguard while resetting nothing. A method body is the one thing an analyzer can read and a runtime check cannot, so this rule reads it. If the carry-over is deliberate, use `[SharedState]` - which claims nothing a body could contradict.

**Why this matters.** The Mann-Whitney U test used for significance assumes samples are independent. When method A warms a shared cache that method B reads, method B's timings are artificially linked to method A running first. The shuffling math breaks and the significance verdict becomes unreliable. This is not a measurement-quality concern - it is a correctness concern for the statistical model.

Typical fixes:

1. Remove the attribute so the class uses `PerMethod`
2. Keep `PerClass` and implement `IStateReset` so shared state is reset between benchmark methods
3. Keep `PerClass` and add `[SharedState]` when the carry-over is deliberate - preferred over a `#pragma`, since it also tells the engine
4. Keep `PerClass` and suppress with `#pragma warning disable NB0011`

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

### NB0014 - Capturing body cannot be isolated

NBenchmark measures a benchmark body in a separate worker process, because the runtime configuration a process starts under is the dominant term in a small measurement. It gets the body there by resolving the method the compiler already emitted; it never serializes or regenerates it.

A lambda that captures state cannot be addressed that way, so the captured *values* are sent instead - but only when the value's measured behaviour is fully determined by the bytes sent. Most ordinary data qualifies and the benchmark is isolated; a value whose behaviour is not determined by its contents (a `Stream`, a collection with a custom comparer, a user type that has not opted in) is refused, and a refusal fails the run.

```csharp
var data = BuildInput();

// Info NB0014: captures 'data'
Benchmark.Run(() => Process(data));
```

Whether a given capture crosses depends on run-time facts a rule cannot see - a collection's comparer, whether two fields alias, the encoded size - so NB0014 reports the capture and points at `IsolationStatus` as the authority. It moves the news to where you can still act on it, and names the symbols responsible, which the runtime cannot do as precisely because by then they are fields on a compiler-generated class.

**It is `Info`, not a warning**, because capturing is the idiomatic way to benchmark over prepared data. Warning on it would push you towards contorted code to silence a build. What it costs is fidelity, not correctness.

**Scope:** the `Benchmark.Run*` family and `BenchmarkSuite.Add(...)`.

A few shapes are worth knowing because they do not read the way they lower:

| Body | Isolated? | Why |
| --- | --- | --- |
| `() => 43` | yes | Nothing to carry. Roslyn still emits it as an instance method on a cached singleton, so a `Target is null` test would get this wrong. |
| `static () => 43` | yes | Same as above - `static` documents the intent, it does not change the lowering. |
| `() => Work(local)` | if `local` can be sent faithfully | An `int`, a `string`, an `int[]` or a record of those is sent by value. A `Stream` is not. |
| `() => Work(_field)` | if the whole object can be sent faithfully | Captures `this` - naming an instance member without a receiver carries the whole object, so every field of it has to qualify. |
| `() => Work(StaticField)` | yes | A static needs no receiver. |
| `widget.Compute` | if `widget` can be sent faithfully | A method group over a live object; the receiver is walked field by field like any other, and rebuilt as the class `widget` actually is rather than the one that declared `Compute`. |
| `() => Work(shapes)` where `shapes` is a `Shape[]` holding a `Square` | no | A collection is sent against its *element* type, so the square would arrive as a shape with its override gone. The elements are checked, and only when the element type is one a subclass could stand in for - an `int[]` or a `string[]` is never walked. |
| `() => 43` beside `() => local` | yes | A non-capturing lambda keeps its isolation even when a sibling in the same scope captures. |

`Add` is where capture reads as most idiomatic, and where it costs most. A suite is addressed as a *set* - one worker measures all of its bodies - so the first body that cannot be addressed takes every sibling in-process with it, including the ones that would have isolated fine on their own. The message says so:

```csharp
var data = BuildInput();

await new BenchmarkSuite("Sorting")
    // Info NB0014: captures 'data' - the whole suite falls back to this process
    .Add("Sort", () => Array.Sort(data))
    .Add("Own", () => { var own = BuildInput(); Array.Sort(own); })
    .RunAsync();
```

One diagnostic is reported per capturing body, and none on a self-contained sibling - so a suite mixing the two shows exactly which bodies to change.

**Parameterized `Add` overloads are covered too.** Parameter values travel as serialized constants, so a sweep is isolated like any other suite and a capture in a parameterized body is the operative cause. A parameterized body that captures nothing stays silent - its parameter is supplied at each invocation rather than closed over.

**The remedy the message names is the prepared-state split**, not a `[BenchmarkPlan]` factory. `Benchmark.Run(prepare: () => Build(), body: d => Use(d))` and `.WithState(() => Build())` let the worker build the state itself, which is one line from what you already wrote; a plan factory is the escape hatch for suites holding something no factory can describe.

The `setup:` and `teardown:` delegates on `Add` are not reported either. They are not measured bodies, and a suite with per-iteration lifecycle is refused isolation for having delegates on the wrong side of the boundary at all.

To isolate the body, move the state inside it:

```csharp
// No capture: the body builds what it needs
Benchmark.Run(() => Process(BuildInput()));
```

That measures the setup too, so it is not always what you want. When it is not, use a `[Benchmark]` class - discovery runs inside the worker, so `[BenchmarkSetup]` and fields are built there and nothing has to cross:

```csharp
public class ProcessBenchmarks
{
    private Input _data = null!;

    [BenchmarkSetup] public void Setup() => _data = BuildInput();

    [Benchmark] public Output Run() => Process(_data);
}
```

**What NB0014 does not catch.** Bodies handed to NBenchmark as method groups over live objects (`Benchmark.Run(widget.Compute)`) are refused at runtime for the same reason, but are not lambdas and so are outside this rule. Raise the severity if you want capture to fail a build:

```ini
[*.cs]
dotnet_diagnostic.NB0014.severity = warning
```

## Runtime independence warning

In addition to the compile-time analyzers above, NBenchmark emits a runtime warning on every `BenchmarkResult.Warnings` list when a class actually runs under `InstanceLifetime.PerClass` with more than one `[Benchmark]` method and has declared neither `IStateReset` nor `[SharedState]`. This covers suite mode (where analyzers do not run) and cases where the analyzer package is not installed. It is raised by whichever process measured the group, so an isolated worker reports it too - which is the default Harness path.

Declaring the sharing on the class is the preferred opt-out, because it is scoped to the class that means it:

```csharp
[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState] // measuring the warm-cache path is the point
public class CacheBenchmarks { }
```

For a whole run, set `SuppressPerClassIndependenceWarning` to `true` on `MeasurementOptions`:

```csharp
// Suppress the runtime warning for every class in the run
var host = BenchmarkHarness.Create(args)
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

### NB0015 - Conflicting isolation attributes

`[InProcess]` asks for the host process and `[IsolatedProcess]` asks for a dedicated worker. On one member they cannot both be honoured, so the combination is an error rather than silently resolved.

```csharp
public class MyBenchmarks
{
    // Error NB0015
    [Benchmark, InProcess, IsolatedProcess]
    public void Both() { }
}
```

A **method-level** attribute overriding a **class-level** one is a different thing and is not reported - it is the documented way to force one benchmark out of a mostly-in-process class:

```csharp
[InProcess]
public class MostlyHere
{
    [Benchmark] public void Here() { }

    // Fine: the method wins over the class.
    [Benchmark, IsolatedProcess] public void There() { }
}
```

Discovery refuses the same combination at runtime, with the same message, for assemblies that do not run the analyzer.

## Severity

Diagnostics use the default severity listed in the table above. The default is chosen by where the problem sits on the invalid-to-suspicious spectrum:

- **Errors** mean the benchmark cannot run or will produce meaningless results. NB0002, NB0003, NB0004, NB0005, NB0006, NB0007, NB0008, NB0009 and NB0015 are errors.
- **Warnings** mean the code can run but the measurements may be invalid. NB0001, NB0010, NB0011, and NB0013 are warnings.
- **Info** means the code and the measurement are both fine, but something about how the measurement was taken is worth knowing. NB0014 is informational.

You can override the severity of any diagnostic in `.editorconfig`. For example, to make all throwaway-lambda warnings errors in Single mode too:

```ini
[*.cs]
dotnet_diagnostic.NB0010.severity = error
```

Or to downgrade harness-mode body-effect errors to warnings in a legacy codebase while you migrate:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = warning
dotnet_diagnostic.NB0005.severity = warning
```
