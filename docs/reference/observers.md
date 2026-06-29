---
title: Measurement Observer
description: Live-telemetry callback surface for streaming measurement events during benchmark execution.
order: 3
---

# Measurement Observer

The `IMeasurementObserver` interface provides a live-telemetry callback surface for streaming measurement events as a benchmark runs. It is **not** a replacement for `IBenchmarkProgress` or `IReporter` - it complements them with sample-level and phase-level telemetry at the engine level.

## Contract

```csharp
public interface IMeasurementObserver
{
    void OnPhase(in MeasurementPhaseEvent e);
    void OnSample(in SampleEvent e);
    void OnDetector(in DetectorStateEvent e);
    void OnResult(BenchmarkResult result);
}
```

All four methods are `void`-returning. The contract is **"return immediately, never block, never allocate on the hot path."** The observer must not throw - doing so is undefined behaviour (the engine does not catch observer exceptions on the hot path).

## Getting started

Attach an observer to a suite or harness:

```csharp
// Suite mode
var suite = new BenchmarkSuite("example")
    .Add("myBenchmark", () => Work())
    .WithObserver(myObserver);

// Harness mode
BenchmarkHarness.Create(args)
    .WithObserver(myObserver);
```

Or attach via `RunSpec.Observer` when using `BenchmarkRunner` directly:

```csharp
var runner = BenchmarkRunner.Instance;
var spec = new RunSpec { Observer = myObserver };
runner.Run("myBenchmark", () => Work(), spec);
```

In Harness mode, observers can also be activated from the CLI via `--observer <name>` (see [CLI Reference](cli.md#--observer-type)). The CLI resolves the name through `ObserverRegistry` - external packages self-register their observers via `[ModuleInitializer]`, the same pattern reporters use.

## Multiple observers

`WithObserver` is additive and repeatable: each call appends another observer rather than replacing the previous one. Multiple attached observers are composed into a `CompositeMeasurementObserver` fan-out so every observer receives every event.

```csharp
var suite = new BenchmarkSuite("example")
    .Add("myBenchmark", () => Work())
    .WithObserver(dashboardObserver)
    .WithObserver(loggingObserver);
```

The composite wraps each per-observer dispatch in a try/catch so one throwing observer cannot kill the stream for the others. The observer contract is "must not throw"; the try/catch is defence-in-depth that isolates a misbehaving observer rather than propagating. Exceptions are traced (via `System.Diagnostics.Trace`) so a host with a `TraceListener` attached can see why an observer stopped receiving events.

The same stacking applies to the CLI: `--observer live --observer logging` composes both observers into a single fan-out. The `--observer` flag is repeatable, mirroring `--reporter`.

## Default

If no observer is attached, `NullMeasurementObserver.Instance` (a no-op singleton) is used. When unattached, the engine performs a single reference comparison (`observer != NullMeasurementObserver.Instance`) per hot-path entry and skips all struct construction. This is the zero-cost fast path. An empty observer list (no `WithObserver` calls and no `--observer` flags) collapses to the null singleton via `ResolveObserver()`, so the hot-path guard stays false and the loop pays no dispatch cost.

## Event types

All event types are `readonly record struct` (stack-allocated, value equality).

### MeasurementPhaseEvent

```csharp
public readonly record struct MeasurementPhaseEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    PhaseTransition Transition,
    double? JitterMetric = null,
    bool DetectorSwitched = false,
    int? ResolvedK = null,
    int? ResolvedWarmup = null,
    WarmupStopReason? WarmupStop = null,
    SampleStopReason? SampleStop = null);
```

- `Phase`: one of `Jitter`, `Calibration`, `Warmup`, `Measurement`
- `Transition`: `Starting` or `Completed`
- `JitterMetric`: present only for Phase 0 `Completed` events (null otherwise)
- `DetectorSwitched`: meaningful only for Phase 0 `Completed` events (true = IQR -> MAD auto-switch)
- `ResolvedK`: set on `Calibration` completed; the calibrated ops-per-sample count (null when calibration was skipped)
- `ResolvedWarmup`: set on `Warmup` completed; the number of warmup iterations that ran (null when warmup was skipped)
- `WarmupStop`: set on `Warmup` completed; why warmup stopped (`ExplicitCount`, `Settled`, `MaxCeiling`, `WallClockCap`)
- `SampleStop`: set on `Measurement` completed; why measurement stopped (`ExplicitCount`, `CiTargetMet`, `MaxCeiling`, `WallClockCap`)

### SampleEvent

```csharp
public readonly record struct SampleEvent(
    string BenchmarkName,
    int Ordinal,
    double PerOpNs,
    int K,
    long AllocDelta,
    bool Warmup);
```

`Warmup` distinguishes calibration/warmup samples (`true`) from measured samples (`false`). In calibration phase, samples are emitted per-probe with the calibration `K` value. In warmup/measurement, samples are emitted with the resolved `K`.

### DetectorStateEvent

```csharp
public readonly record struct DetectorStateEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    int SampleCount,
    double Mean,
    double StdDev,
    double CiHalfWidth,
    int CurrentK);
```

Emitted after detector updates. During calibration, `Mean`/`StdDev`/`CiHalfWidth` reflect the calibrator's probe readings (the CI fields are not meaningful until measurement). During measurement, this event is emitted when the stop rule resolves (or at phase completion fallback) and `CiHalfWidth` provides the convergence signal.

### BenchmarkResult

The final result fires once per benchmark from `BenchmarkRunner.OnResult`. It contains the runner-assembled per-benchmark statistics and diagnostics (before any suite-level post-processing such as cross-benchmark significance grouping).

## Lifecycle of events

A typical benchmark with auto-warmup and auto-measurement emits events in this order:

1. `OnPhase(MeasurementPhase.Jitter, Starting)` - if jitter calibration is enabled
2. `OnPhase(MeasurementPhase.Jitter, Completed)` - with JitterMetric and DetectorSwitched
3. `OnPhase(MeasurementPhase.Calibration, Starting)` - if OpsPerSample is null (auto)
4. `OnSample(Warmup=true)` - one per calibration probe
5. `OnDetector(Calibration)` - after each calibration step (one calibrated `K` candidate)
6. `OnPhase(MeasurementPhase.Calibration, Completed)` - with ResolvedK
7. `OnPhase(MeasurementPhase.Warmup, Starting)`
8. `OnSample(Warmup=true)` - throttled per batch
9. `OnPhase(MeasurementPhase.Warmup, Completed)` - with WarmupStop and ResolvedWarmup
10. `OnPhase(MeasurementPhase.Measurement, Starting)`
11. `OnSample(Warmup=false)` - throttled per batch
12. `OnDetector(Measurement)` - when measurement resolves (CI target met or max ceiling)
13. `OnPhase(MeasurementPhase.Measurement, Completed)` - with SampleStop
14. `OnResult(result)` - final assembled BenchmarkResult

When `OpsPerSample` is pinned (calibration skipped) or `WarmupIterations=0` (warmup skipped), the corresponding phases are omitted.

## Throttling

Sample events are throttled by `ProgressCadence(n) = Math.Min(Math.Max(1, n / 20), 50)` where `n` is the current sample count. For 5 samples all emit; for 100,000 samples every 50th emits. This prevents the observer from dominating the hot path on long runs.

## Thread safety

All observer calls are made from the single measurement thread. Implementations are not required to be thread-safe. If a consumer needs cross-thread access, it must synchronise internally (e.g. via `Channel<T>` in Phase 2).

## Implementation guide

A custom observer that logs phase transitions and samples:

```csharp
using NBenchmark;

public class LoggingObserver : IMeasurementObserver
{
    public void OnPhase(in MeasurementPhaseEvent e)
    {
        Console.WriteLine($"[{e.BenchmarkName}] Phase {e.Phase} {e.Transition}");
    }

    public void OnSample(in SampleEvent e)
    {
        if (!e.Warmup)
            Console.WriteLine($"[{e.BenchmarkName}] Sample #{e.Ordinal}: {e.PerOpNs:F2} ns/op");
    }

    public void OnDetector(in DetectorStateEvent e)
    {
        Console.WriteLine($"[{e.BenchmarkName}] Detector [{e.Phase}]: " +
            $"n={e.SampleCount}, mean={e.Mean:F2}, ci%={e.CiHalfWidth * 100:F2}");
    }

    public void OnResult(BenchmarkResult result)
    {
        Console.WriteLine($"[{result.Name}] Mean: {result.Statistics.Mean}");
    }
}
```

**Important**: all four methods must return immediately and never allocate on the hot path. If you need to persist telemetry, buffer it (e.g. via a pre-allocated ring buffer or `System.Threading.Channels.Channel<T>` for Phase 2 back-pressure) and flush it asynchronously outside the observer call.

## See also

- `docs/reference/bcl-instrumentation.md` - the `System.Diagnostics` Meter/ActivitySource instrumentation.
- `docs/reference/configuration.md` - the `MeasurementOptions` surface.
