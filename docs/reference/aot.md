---
title: Trimming and Native AOT
description: What NBenchmark supports when you publish with PublishTrimmed or PublishAot, and why.
order: 6
---

# Trimming and Native AOT

NBenchmark is annotated for trimming and Native AOT, but it is not trim-safe or AOT-safe, and it cannot be. This page says exactly which parts warn, which parts work, and what happens if you publish anyway.

## The short version

| You publish with | What you get |
| --- | --- |
| Nothing (the default) | Everything works. Most benchmark projects are console apps that are never published this way. |
| `PublishTrimmed=true` | An `IL2026` warning at every NBenchmark call, naming the feature that needs reflection. The app builds. Whether it still measures correctly depends on what the trimmer removed. |
| `PublishAot=true` | The same warnings plus `IL3050`, and the run fails at the first benchmark with a `NotSupportedException` naming `MakeGenericMethod`. |

If you only need the numbers, benchmark your code from an ordinary, non-published project. Publishing a benchmark host with AOT measures the AOT-compiled version of your code only if the worker is also AOT-compiled, which it is not - see [Isolated runs](../features/isolated-runs.md).

## Why it cannot be clean

Three parts of the design are reflective, and each is reflective for a reason that a trim-safe rewrite would have to give up:

- **Discovery.** Harness mode finds `[Benchmark]` methods by walking an assembly's types. Trimming removes types nothing statically references, which is precisely the set discovery is looking for.
- **Argument binding.** `[BenchmarkCase]` values are bound to parameters whose types are only known once the method is found, which means constructing generic types and methods at run time.
- **The worker protocol.** An isolated run moves the body's closure and its prepared state to another process with the reflection-based JSON serializer. Refusing to do that would mean refusing isolation, and isolation is what makes the numbers worth reading.

The engine's own measurement loop is none of those things. It is the plumbing around it that reflects.

## The annotations

Every reflective member declares itself, so the compiler tells you which feature you are asking for rather than emitting a generic warning:

```text
warning IL3050: Using member 'NBenchmark.Benchmark.Run<T>(...)' which has 'RequiresDynamicCodeAttribute':
A benchmark run reflects over the body's closure and its prepared state and moves both to the
measuring process with the reflection-based JSON serializer, so trimming or AOT compiling the host
can change or break what is measured.
```

Two things follow from that. Warnings you see are declared and expected - suppress them with `NoWarn` if you have decided to accept them. Warnings you do not see are a promise: the trim and AOT analyzers run on every NBenchmark build, so an undeclared reflective path is a build break here rather than a surprise in your published app.

`NBenchmark` is marked `IsTrimmable`, which is only true because those annotations exist.

## Single mode under Native AOT

The obvious question is whether the simplest use - `Benchmark.Run(() => ...)` over a non-capturing lambda, measured in this process - survives AOT. Today it does not.

The publish succeeds. The run then fails, cleanly:

```text
System.NotSupportedException: 'NBenchmark.Engine.BenchmarkRunner.Run[System.Int32](...)' is missing
native code. MethodInfo.MakeGenericMethod() is not compatible with AOT compilation.
```

The body arrives at the engine as a `Delegate` and its result type is recovered at run time, so the typed entry point is reached through `MakeGenericMethod`. That indirection is what keeps a `Func<int>` body from being measured through a `Func<object>` adapter, which would box every return value and charge you an allocation you never wrote. It is a good trade everywhere except here.

This is checked in rather than described: `tests/NBenchmark.AotProbe` publishes with Native AOT and runs the result on every CI build, and the exit code asserts the refusal above. If single mode ever becomes AOT-viable, that probe fails and this page is what gets corrected.

## Single-file publishing

Not supported, and it fails quietly rather than loudly. The measurement worker is located relative to `Assembly.Location`, which is an empty string inside a single-file image, so no worker is found. The consequence is at least labeled: the run falls back to measuring in the host process and the result says `IsolationStatus = InProcessNoWorker` instead of claiming an isolation it did not get.

## See also

- [Isolated runs](../features/isolated-runs.md) - what the worker process does, and what in-process measurement costs you.
- [Analyzers](./analyzers.md) - the build-time diagnostics that ship in the package.
