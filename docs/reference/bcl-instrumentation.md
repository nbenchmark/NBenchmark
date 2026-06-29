# BCL Instrumentation (Meter + ActivitySource)

NBenchmark emits first-class `System.Diagnostics` BCL instrumentation from the same emit points that feed `IMeasurementObserver`. No NuGet packages are required -- `Meter` and `ActivitySource` are part of the .NET BCL since .NET 8. When no OpenTelemetry SDK or listener is attached, the BCL internal checks ensure near-zero overhead.

## Instrument naming

All instrument and tag names use the `nbenchmark.*` namespace for OpenTelemetry compatibility:

| Instrument | Type | Unit | Description |
|---|---|---|---|
| `nbenchmark.sample.duration` | Histogram | ns/op | Per-op sample duration |
| `nbenchmark.alloc.bytes_per_op` | Histogram | B/op | Per-op allocation delta (recorded per sample) |
| `nbenchmark.outliers.removed` | Counter | samples | Cumulative outliers removed |
| `nbenchmark.outliers.removed_total` | ObservableGauge | samples | Running total of removed outliers |
| `nbenchmark.jitter.detector_switches` | Counter | switches | Outlier-detector auto-switches triggered by jitter |
| `nbenchmark.gc.gen0` | Counter | collections | Generation 0 GC collections during measurement |
| `nbenchmark.gc.gen1` | Counter | collections | Generation 1 GC collections during measurement |
| `nbenchmark.gc.gen2` | Counter | collections | Generation 2 GC collections during measurement |
| `nbenchmark.ci.relative_half_width` | ObservableGauge | ratio | CI relative half-width of the running mean |
| `nbenchmark.jitter.metric` | ObservableGauge | ratio | Host jitter metric (MAD / median) |
| `nbenchmark.sample.mean_per_op` | ObservableGauge | ns/op | Running mean per-op duration |
| `nbenchmark.ops_per_second` | ObservableGauge | ops/s | Running operations per second (1e9 / mean per-op ns) |
| `nbenchmark.samples.count` | ObservableGauge | samples | Running sample count |

## Trace span hierarchy

NBenchmark emits nested `Activity` spans that render the autotune lifecycle as a flame-graph-shaped trace:

```
benchmark.suite
  └── benchmark.run
        ├── nbenchmark.phase.jitter
        ├── nbenchmark.phase.calibration
        ├── nbenchmark.phase.warmup
        └── nbenchmark.phase.measurement
```

- `benchmark.suite` (root): created at `OnSuiteStarting`, tags include `nbenchmark.suite.name`, `nbenchmark.suite.benchmark_count`, `nbenchmark.profile`, `nbenchmark.runtime`, `nbenchmark.seed`, `nbenchmark.run_order`; stopped at `OnSuiteCompleted` with `nbenchmark.suite.result_count`.
- `benchmark.run` (per-benchmark): created at `OnBenchmarkRunStarting`, tags include `nbenchmark.name`, `nbenchmark.class`, `nbenchmark.baseline`, `nbenchmark.parameter_set`; stopped at `OnBenchmarkRunCompleted` with `nbenchmark.result.median_ns`, `nbenchmark.result.mean_ns`, `nbenchmark.result.sample_count`, `nbenchmark.result.outliers_removed`.

### Phase spans

Each phase transition creates an Activity span named `nbenchmark.phase.<phase>` where `<phase>` is one of `jitter`, `calibration`, `warmup`, or `measurement`. Phase spans nest under their parent `benchmark.run` span. Tags include:

| Tag | Set on | Value |
|---|---|---|
| `nbenchmark.benchmark.name` | start + stop | Benchmark name |
| `nbenchmark.phase` | start + stop | Phase enum name |
| `nbenchmark.sample_stop_reason` | stop (measurement) | Why measurement ended |
| `nbenchmark.warmup_stop_reason` | stop (warmup) | Why warmup ended |
| `nbenchmark.resolved_k` | stop (calibration) | Calibrated ops-per-sample count |
| `nbenchmark.resolved_warmup` | stop (warmup) | Resolved warmup iteration count |
| `nbenchmark.jitter_metric` | stop (jitter) | Host jitter metric value |
| `nbenchmark.detector_switched` | stop (jitter) | Whether the outlier detector was auto-switched |

### Span events

Span events are discrete annotations on a phase span that explain *why* a phase ended. A trace UI renders these as markers on the flame-graph row, making the autotune decision visible at a glance:

| Event | Parent span | Fired when | Key tags |
|---|---|---|---|
| `detector.switched` | `nbenchmark.phase.jitter` | The outlier detector auto-switched IQR -> MAD | `nbenchmark.from`, `nbenchmark.to`, `nbenchmark.jitter_metric` |
| `warmup.plateau_reached` | `nbenchmark.phase.warmup` | Warmup stopped because the body settled (plateau rule) | - |
| `measurement.ci_target_met` | `nbenchmark.phase.measurement` | Measurement stopped because the CI half-width target was met | `nbenchmark.achieved_ci_width`, `nbenchmark.ci_target` |
| `phase.cap_hit` | `nbenchmark.phase.warmup` / `nbenchmark.phase.measurement` | A phase ended early at the wall-clock tuning cap | - |

## Getting started with OpenTelemetry

Install the OpenTelemetry SDK and the OTLP exporter:

```bash
dotnet add package OpenTelemetry
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

Then configure in your application:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("NBenchmark")
    .AddOtlpExporter()
    .Build();

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("NBenchmark")
    .AddOtlpExporter()
    .Build();
```

All NBenchmark instruments are automatically picked up when a Meter named `NBenchmark` or an ActivitySource named `NBenchmark` is subscribed to.

## See also

- `docs/reference/observers.md` - the `IMeasurementObserver` interface and event types.
- `docs/statistics/diagnostics.md` - runtime diagnostics counters (GC, heap, exceptions, CPU).
