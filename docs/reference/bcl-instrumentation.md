---
title: BCL Instrumentation
description: First-class System.Diagnostics Meter and ActivitySource instrumentation emitted during benchmark execution.
order: 4
---

# BCL Instrumentation

NBenchmark emits first-class `System.Diagnostics` BCL instrumentation from the same emit points that feed `IMeasurementObserver`. No NuGet packages are required, as `Meter` and `ActivitySource` have been part of the .NET BCL since .NET 8. When no OpenTelemetry SDK or listener is attached, the BCL internal checks ensure near-zero overhead.

## Instrument naming

All instrument and tag names use the `nbenchmark.*` namespace for OpenTelemetry compatibility:

| Instrument | Type | Unit | Description |
| --- | --- | --- | --- |
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

```text
benchmark.suite                      the coordinator
  └── nbenchmark.worker              one per measuring process; absent in an --in-process run
        └── benchmark.run
              ├── nbenchmark.phase.jitter
              ├── nbenchmark.phase.calibration
              ├── nbenchmark.phase.warmup
              └── nbenchmark.phase.measurement
```

- `benchmark.suite` (root): Created at `OnSuiteStarting`. Tags include `nbenchmark.suite.name`, `nbenchmark.suite.benchmark_count`, `nbenchmark.profile`, `nbenchmark.runtime`, `nbenchmark.seed`, and `nbenchmark.run_order`. It stops at `OnSuiteCompleted` with `nbenchmark.suite.result_count`.
- `nbenchmark.worker` (per measuring process): Opened as a worker's session begins. It is parented to the coordinator's span through the `TRACEPARENT` it inherited and is tagged with `nbenchmark.worker.pid` plus the run's resource attributes. This span ensures an isolated run appears as a single trace rather than one trace per process, and it renders worker startup as the gap before the first phase. An `--in-process` run has no such process or span.
- `benchmark.run` (per-benchmark): Created at `OnBenchmarkRunStarting`. Tags include `nbenchmark.name`, `nbenchmark.class`, `nbenchmark.baseline`, and `nbenchmark.parameter_set`. It stops at `OnBenchmarkRunCompleted` with `nbenchmark.result.median_ns`, `nbenchmark.result.mean_ns`, `nbenchmark.result.sample_count`, and `nbenchmark.result.outliers_removed`.

### Phase spans

Each phase transition creates an `Activity` span named `nbenchmark.phase.<phase>`, where `<phase>` is one of `jitter`, `calibration`, `warmup`, or `measurement`. Phase spans nest under their parent `benchmark.run` span.

| Tag | Set on | Value |
| --- | --- | --- |
| `nbenchmark.benchmark.name` | start + stop | Benchmark name |
| `nbenchmark.phase` | start + stop | Phase enum name |
| `nbenchmark.sample_stop_reason` | stop (measurement) | Why measurement ended |
| `nbenchmark.warmup_stop_reason` | stop (warmup) | Why warmup ended |
| `nbenchmark.resolved_k` | stop (calibration) | Calibrated ops-per-sample count |
| `nbenchmark.resolved_warmup` | stop (warmup) | Resolved warmup sample count |
| `nbenchmark.jitter_metric` | stop (jitter) | Host jitter metric value |
| `nbenchmark.detector_switched` | stop (jitter) | Whether the outlier detector was auto-switched |

### Span events

Span events are discrete annotations on a phase span that explain why a phase ended. A trace UI renders these as markers on the flame-graph row, making the autotune decision visible.

| Event | Parent span | Fired when | Key tags |
| --- | --- | --- | --- |
| `detector.switched` | `nbenchmark.phase.jitter` | The outlier detector auto-switched IQR $\rightarrow$ MAD | `nbenchmark.from`, `nbenchmark.to`, `nbenchmark.jitter_metric` |
| `warmup.plateau_reached` | `nbenchmark.phase.warmup` | Warmup stopped because the body settled (plateau rule) | - |
| `measurement.ci_target_met` | `nbenchmark.phase.measurement` | Measurement stopped because the CI half-width target was met | `nbenchmark.achieved_ci_width`, `nbenchmark.ci_target` |
| `phase.cap_hit` | `nbenchmark.phase.warmup` / `nbenchmark.phase.measurement` | A phase ended early at the wall-clock tuning cap | - |

## Getting started with OpenTelemetry

Install the exporter package:

```bash
dotnet add package NBenchmark.Exporters.OpenTelemetry
```

Then point the exporter at a collector. You can do this from the command line:

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
```

Or in code:

```csharp
using NBenchmark.Exporters.OpenTelemetry;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithOpenTelemetry(o => o.Endpoint = "http://localhost:4317")
    .RunAsync();
```

The package manages the OpenTelemetry SDK, subscribes it to the `NBenchmark` `Meter` and `ActivitySource`, applies histogram buckets suited to per-op durations, and flushes when the run ends. Crucially, it does this in the harness **and inside every isolated worker process**.

If no endpoint is configured (via command line, code, or `OTEL_EXPORTER_OTLP_ENDPOINT`), the exporter declines to build and nothing connects. Referencing the package alone does not request an export.

For a runnable version with a Docker Compose file for Grafana, Prometheus, and Tempo, see [Samples](../samples.md#telemetry-sample).

### Configuration

The `WithOpenTelemetry(...)` method accepts transport settings, which map to the corresponding OTel-standard environment variables:

| Option | Environment variable |
| --- | --- |
| `Endpoint` | `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `Protocol` | `OTEL_EXPORTER_OTLP_PROTOCOL` |
| `Headers` | `OTEL_EXPORTER_OTLP_HEADERS` |
| `TimeoutMilliseconds` | `OTEL_EXPORTER_OTLP_TIMEOUT` |
| `ServiceName` | `OTEL_SERVICE_NAME` |
| `ResourceAttributes` | `OTEL_RESOURCE_ATTRIBUTES` |

Configuration must be expressed as environment variables because a worker process runs none of your code. The environment is the only channel that reaches the worker.

Instrument shaping (such as histogram bucket boundaries and the metric export interval) is handled by the package code on both sides, applying the same defaults.

### Subscribing without the package

Because the instrumentation is plain BCL, you can attach your own listener if you run NBenchmark in-process:

```csharp
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("NBenchmark")
    .AddOtlpExporter()
    .Build();

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("NBenchmark")
    .AddOtlpExporter()
    .Build();
```

All NBenchmark instruments are picked up when a `Meter` or `ActivitySource` named `NBenchmark` is subscribed to. This is sufficient for an `--in-process` run, but not for isolated runs, as the SDK would need to be constructed inside the worker process.

## Resource attributes

Every `benchmark.suite` span is stamped with resource attributes that identify the run across commits, branches, CI pipelines, and machines. This allows backends (such as Grafana, Jaeger, or Honeycomb) to render cross-commit trend lines and regression alarms.

Attributes are read once per process from environment variables and cached.

### CI identification

| Attribute | Source env vars |
| --- | --- |
| `nbenchmark.ci_provider` | `GITHUB_ACTIONS`, `GITLAB_CI`, `AZURE_PIPELINES`/`TF_BUILD`, `CIRCLECI`, `APPVEYOR`, `TEAMCITY_VERSION`, `JENKINS_URL`, `TRAVIS`, `BUILDKITE` |
| `nbenchmark.ci_run_id` | `GITHUB_RUN_ID`, `CI_PIPELINE_ID`, `BUILD_BUILDID`, `CIRCLE_BUILD_NUM`, `APPVEYOR_BUILD_ID`, `TEAMCITY_BUILDID`, `BUILDKITE_BUILD_ID`, `TRAVIS_BUILD_ID` |
| `nbenchmark.ci_run_url` | `GITHUB_SERVER_URL`, `CI_JOB_URL`, `BUILD_BUILDURI`, `CIRCLE_BUILD_URL` |
| `nbenchmark.ci_repository` | `GITHUB_REPOSITORY`, `CI_REPOSITORY_URL` |
| `nbenchmark.ci_ref` | `GITHUB_REF`, `CI_COMMIT_REF_NAME` |
| `nbenchmark.ci_attempt` | `GITHUB_RUN_ATTEMPT` |

### Git identification

| Attribute | Source env vars | Fallback |
| --- | --- | --- |
| `nbenchmark.commit_sha` | `GITHUB_SHA`, `CI_COMMIT_SHA`, `GIT_COMMIT` | `git rev-parse --short HEAD` |
| `nbenchmark.branch` | `GITHUB_HEAD_REF`, `CI_COMMIT_BRANCH`, `GIT_BRANCH` | `git rev-parse --abbrev-ref HEAD` |

CI-sourced values take precedence over the git CLI fallback.

### Host identification

| Attribute | Value |
| --- | --- |
| `nbenchmark.host.machine_name` | `Environment.MachineName` |
| `nbenchmark.host.os` | `windows`, `macos`, or `linux` |
| `nbenchmark.host.arch` | `arm64`, `x64`, `x86`, etc. |
| `nbenchmark.host.runtime` | `RuntimeInformation.FrameworkDescription` (e.g., `.NET 8.0.22`) |

### OpenTelemetry-standard env vars

NBenchmark honors `OTEL_RESOURCE_ATTRIBUTES` and `OTEL_SERVICE_NAME` verbatim. `OTEL_RESOURCE_ATTRIBUTES` is parsed as a comma-separated `key=value` list and copied onto the span. `OTEL_SERVICE_NAME` maps to `service.name`.

## Cross-process streaming

By default, benchmarks are measured in a separate `nbworker` process. While your registered `IMeasurementObserver` and `IBenchmarkProgress` instances still fire via a pipe, OTLP requires its own channel to a collector.

### Building the SDK inside the worker

An SDK constructed in your `Main` method is in the wrong process. Because phase spans and per-sample metrics are emitted inside the worker, the SDK must also be constructed there.

`NBenchmark.Exporters.OpenTelemetry` handles this by ensuring:
1. **The exporter registers from a `[ModuleInitializer]`**, which the worker runs after loading the necessary packages.
2. **Configuration arrives as environment variables**, as this is the only channel that reaches the worker.
3. **`System.Diagnostics.DiagnosticSource` is unified**. The SDK and the engine must subscribe to the same registry to avoid missing events.

### Env-var forwarding

`MeasurementBudget.ApplyTelemetryEnvironment` writes the following variables into every worker's environment block before it starts:

| Env var | Purpose |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP exporter endpoint |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP transport (`grpc` or `http/protobuf`) |
| `OTEL_EXPORTER_OTLP_HEADERS` | OTLP exporter headers (such as auth) |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | OTLP export timeout |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes (passed through) |
| `OTEL_SERVICE_NAME` | Service name (passed through) |
| `NBENCHMARK_OTEL_ENDPOINT` | NBenchmark-specific endpoint mirror |
| `TRACEPARENT` | W3C trace context of the coordinator's span, allowing worker spans to join the run's trace |

If `NBENCHMARK_OTEL_ENDPOINT` is set and `OTEL_EXPORTER_OTLP_ENDPOINT` is not, NBenchmark applies the mirror so that the SDK picks it up.

### `--otlp-endpoint` CLI flag

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
```

The harness mirrors this into `OTEL_EXPORTER_OTLP_ENDPOINT` before spawning isolated workers. If `OTEL_EXPORTER_OTLP_ENDPOINT` is already set, the CLI flag does not override it.

### Observer forwarding

Auto-attached observers fire in both the worker and the coordinator. The worker resolves the `ObserverRegistry` auto-attach list once per session.

Explicit observers (via `--observer <name>`) are **not** forwarded to workers; they fire in the coordinator only. Programmatic observers (`WithObserver`) are live objects and cannot cross the process boundary; the coordinator replays the event stream into your instance.

### Topology

```text
In-process / local dev:
  AdaptiveLoop -> Observer shim -> Embedded web host -> React SPA in browser

Isolated / CI:
  Worker       -> OTLP -> Collector      (exporter built inside the worker)
  Host process -> OTLP -> Collector      (suite span, and the trace context the workers inherit)
  Collector -> Grafana / Jaeger / Honeycomb
```

Both modes produce a single trace and look identical to a dashboard.

## See also

- [Measurement Observer](./observers.md) - The `IMeasurementObserver` interface and event types.
- [CLI Reference](./cli.md) - The `--otlp-endpoint` CLI flag.
- [Diagnostics](../statistics/diagnostics.md) - Runtime diagnostics counters.
