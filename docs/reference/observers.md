---
title: Measurement Observer
description: Live-telemetry callback surface for streaming measurement events during benchmark execution.
order: 3
---

# Measurement Observer

The `IMeasurementObserver` interface provides a live-telemetry callback surface for streaming measurement events as a benchmark runs. It complements `IBenchmarkProgress` and `IReporter` by providing sample-level and phase-level telemetry at the engine level.

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

All four methods return `void`. You must follow this contract: **return immediately, never block, and never allocate on the hot path.** The observer must not throw; doing so is undefined behavior, as the engine does not catch observer exceptions on the hot path.

## Setup

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

`WithObserver` is the only way to attach one: the engine's per-benchmark runner is internal, so a Single-mode `Benchmark.Run` call cannot take an observer. Wrap the body in a one-benchmark suite when you need sample-level telemetry for a single measurement.

In Harness mode, you can activate observers from the CLI using the `--observer <name>` flag (see [CLI Reference](cli.md#diagnostics)). The CLI resolves the name through `ObserverRegistry`. External packages self-register their observers via `[ModuleInitializer]`, mirroring the pattern used by reporters.

## Multiple observers

The `WithObserver` method is additive. Each call appends another observer rather than replacing the previous one. NBenchmark composes multiple attached observers into a `CompositeMeasurementObserver` fan-out so every observer receives every event.

```csharp
var suite = new BenchmarkSuite("example")
    .Add("myBenchmark", () => Work())
    .WithObserver(dashboardObserver)
    .WithObserver(loggingObserver);
```

The composite observer wraps each per-observer dispatch in a try/catch block so one throwing observer cannot kill the stream for others. While observers must not throw, this provides defense-in-depth. Exceptions are logged via `System.Diagnostics.Trace`.

The same stacking applies to the CLI: `--observer live --observer logging` composes both observers into a single fan-out.

## Default behavior

If no observer is attached, NBenchmark uses `NullMeasurementObserver.Instance` (a no-op singleton). When unattached, the engine performs a single reference comparison per hot-path entry and skips all struct construction. This is a zero-cost fast path.

## Event types

All event types are `readonly record struct` (stack-allocated with value equality).

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

- `Phase`: One of `Jitter`, `Calibration`, `Warmup`, or `Measurement`.
- `Transition`: `Starting` or `Completed`.
- `JitterMetric`: Present only for `Phase 0` `Completed` events.
- `DetectorSwitched`: Meaningful only for `Phase 0` `Completed` events (`true` = IQR $\rightarrow$ MAD auto-switch).
- `ResolvedK`: Set on `Calibration` completed; the calibrated ops-per-sample count.
- `ResolvedWarmup`: Set on `Warmup` completed; the number of warmup samples that ran.
- `WarmupStop`: Set on `Warmup` completed; why warmup stopped (e.g., `ExplicitCount`, `Settled`, `MaxCeiling`, `WallClockCap`).
- `SampleStop`: Set on `Measurement` completed; why measurement stopped (e.g., `ExplicitCount`, `CiTargetMet`, `MaxCeiling`, `WallClockCap`).

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

The `Warmup` field distinguishes calibration/warmup samples (`true`) from measured samples (`false`). In the calibration phase, samples are emitted per-probe with the calibration `K` value. In warmup and measurement, samples are emitted with the resolved `K`.

### DetectorStateEvent

```csharp
public readonly record struct DetectorStateEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    int SampleCount,
    double MeanNs,
    double StdDev,
    double CiHalfWidth,
    int CurrentK);
```

NBenchmark emits this event after detector updates. During calibration, `MeanNs`, `StdDev`, and `CiHalfWidth` reflect the calibrator's probe readings. During measurement, this event is emitted when the stop rule resolves (or at phase completion fallback), and `CiHalfWidth` provides the convergence signal.

### BenchmarkResult

The final result fires once per benchmark, carrying the per-benchmark statistics and diagnostics as the engine assembled them - before any suite-level post-processing such as baseline ratios or significance.

## Event lifecycle

A typical benchmark with auto-warmup and auto-measurement emits events in this order:

1. `OnPhase(MeasurementPhase.Jitter, Starting)` - if jitter calibration is enabled.
2. `OnPhase(MeasurementPhase.Jitter, Completed)` - with `JitterMetric` and `DetectorSwitched`.
3. `OnPhase(MeasurementPhase.Calibration, Starting)` - if `OpsPerSample` is null.
4. `OnSample(Warmup=true)` - one per calibration probe.
5. `OnDetector(Calibration)` - after each calibration step.
6. `OnPhase(MeasurementPhase.Calibration, Completed)` - with `ResolvedK`.
7. `OnPhase(MeasurementPhase.Warmup, Starting)`.
8. `OnSample(Warmup=true)` - throttled per batch.
9. `OnPhase(MeasurementPhase.Warmup, Completed)` - with `WarmupStop` and `ResolvedWarmup`.
10. `OnPhase(MeasurementPhase.Measurement, Starting)`.
11. `OnSample(Warmup=false)` - throttled per batch.
12. `OnDetector(Measurement)` - when measurement resolves.
13. `OnPhase(MeasurementPhase.Measurement, Completed)` - with `SampleStop`.
14. `OnResult(result)` - final assembled `BenchmarkResult`.

If `OpsPerSample` is pinned or `WarmupSamples` is 0, the corresponding phases are omitted.

## Isolated runs

By default, benchmarks are measured in a separate `nbworker` process, while the observer lives in your process. The worker streams events back over its pipe and your process replays them. Not every callback crosses the process boundary:

| Callback | Isolated (default) | `--in-process` / `RunInProcess` |
| --- | --- | --- |
| `OnPhase` | ✅ every transition | ✅ |
| `OnDetector` | ✅ every snapshot | ✅ |
| `OnResult` | ✅ once per benchmark | ✅ |
| `OnSample` | ⬜ opt in with `--stream-samples` | ✅ |

`OnSample` is off by default because of the high volume of events. Every other event is emitted only a few times per benchmark. `OnDetector` is not in this class because a benchmark emits only a few snapshots, and it crosses unconditionally to provide the live convergence curve.

The result remains complete regardless of the flag. The worker computes all statistics over the full sample array and ships the samples on the completion frame, so `BenchmarkResult.RawSamples` is complete (subject to `MaxRawSamples`).

### Using `--stream-samples`

Turn on the per-sample stream for consumers that need live data (such as a streaming histogram or a live dashboard):

```bash
dotnet run -- --observer live --stream-samples
```

Or programmatically:

```csharp
.WithOptions(o => o with { StreamSamples = true })
```

Your observer receives one `OnSample` call per sample, mirroring an in-process run. The worker batches these events (one frame per 128 samples or per 100 ms) for transport, but the callback remains per-sample.

**Event delivery**:
- **Bursts**: Events may lag the body by up to one batch. The buffer is flushed ahead of every phase and detector boundary, so phase samples always arrive before the completion event.
- **Throttling**: The stream reflects the engine's throttled subset, not every single reading. This is unrelated to `MaxRawSamples`, which bounds the result array.
- **Requirements**: This flag has no effect without an attached observer.

Streaming samples has no measurable impact on timings because `OnSample` fires outside the timed region and encoding happens on the worker's outbound queue.

For more information, see [Isolated runs](../features/isolated-runs.md).

## Throttling

Sample events are throttled by `ProgressCadence(n) = Math.Min(Math.Max(1, n / 20), 50)`, where `n` is the current sample count. For example, all of the first 20 samples emit, but for 100,000 samples, only every 50th emits. This prevents the observer from dominating the hot path.

## Thread safety

All observer calls are made from the single measurement thread. Implementations do not need to be thread-safe. If you need cross-thread access, synchronize internally (for example, using a `Channel<T>`).

## Implementation guide

Example of a custom observer that logs phase transitions and samples:

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
            $"n={e.SampleCount}, mean={e.MeanNs:F2}, ci%={e.CiHalfWidth * 100:F2}");
    }

    public void OnResult(BenchmarkResult result)
    {
        Console.WriteLine($"[{result.Name}] MeanNs: {result.Statistics.MeanNs}");
    }
}
```

**Important**: All four methods must return immediately and never allocate on the hot path. To persist telemetry, buffer it (using a pre-allocated ring buffer or a `System.Threading.Channels.Channel<T>`) and flush it asynchronously.

## See also

- [BCL instrumentation](./bcl-instrumentation.md) - The `System.Diagnostics` Meter/ActivitySource instrumentation.
- [Configuration](./configuration.md) - The `MeasurementOptions` surface.
