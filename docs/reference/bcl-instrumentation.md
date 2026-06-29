---
title: BCL Instrumentation
description: First-class System.Diagnostics Meter and ActivitySource instrumentation emitted during benchmark execution.
order: 4
---

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

## Resource attributes

Every `benchmark.suite` span is stamped with resource attributes that identify the run across commit, branch, CI pipeline, and machine. A downstream backend (Grafana, Jaeger, Honeycomb) can join on these to render cross-commit trend lines and regression alarms without NBenchmark shipping its own storage layer.

The attributes are read once per process from environment variables and cached for the process lifetime.

### CI identification

| Attribute | Source env vars |
|---|---|
| `nbenchmark.ci_provider` | `GITHUB_ACTIONS`, `GITLAB_CI`, `AZURE_PIPELINES`/`TF_BUILD`, `CIRCLECI`, `APPVEYOR`, `TEAMCITY_VERSION`, `JENKINS_URL`, `TRAVIS`, `BUILDKITE` |
| `nbenchmark.ci_run_id` | `GITHUB_RUN_ID`, `CI_PIPELINE_ID`, `BUILD_BUILDID`, `CIRCLE_BUILD_NUM`, `APPVEYOR_BUILD_ID`, `TEAMCITY_BUILDID`, `BUILDKITE_BUILD_ID`, `TRAVIS_BUILD_ID` |
| `nbenchmark.ci_run_url` | `GITHUB_SERVER_URL`, `CI_JOB_URL`, `BUILD_BUILDURI`, `CIRCLE_BUILD_URL` |
| `nbenchmark.ci_repository` | `GITHUB_REPOSITORY`, `CI_REPOSITORY_URL` |
| `nbenchmark.ci_ref` | `GITHUB_REF`, `CI_COMMIT_REF_NAME` |
| `nbenchmark.ci_attempt` | `GITHUB_RUN_ATTEMPT` |

### Git identification

| Attribute | Source env vars | Fallback |
|---|---|---|
| `nbenchmark.commit_sha` | `GITHUB_SHA`, `CI_COMMIT_SHA`, `GIT_COMMIT` | `git rev-parse --short HEAD` |
| `nbenchmark.branch` | `GITHUB_HEAD_REF`, `CI_COMMIT_BRANCH`, `GIT_BRANCH` | `git rev-parse --abbrev-ref HEAD` (detached HEAD produces no branch attribute) |

CI-sourced values take precedence over the git CLI fallback. When no CI or git env vars are present and the git CLI is unavailable (or outside a repo), the commit and branch attributes are omitted.

### Host identification

| Attribute | Value |
|---|---|
| `nbenchmark.host.machine_name` | `Environment.MachineName` |
| `nbenchmark.host.os` | `windows`, `macos`, or `linux` |
| `nbenchmark.host.arch` | `arm64`, `x64`, `x86`, etc. |
| `nbenchmark.host.runtime` | `RuntimeInformation.FrameworkDescription` (e.g. `.NET 8.0.22`) |

### OpenTelemetry-standard env vars

`OTEL_RESOURCE_ATTRIBUTES` and `OTEL_SERVICE_NAME` are honoured verbatim. `OTEL_RESOURCE_ATTRIBUTES` is parsed as a comma-separated `key=value` list (the OTel convention) and each pair is copied onto the span. `OTEL_SERVICE_NAME` is mapped to `service.name`. A user who has already configured these for the rest of their service does not need to repeat themselves.

## Cross-process streaming

Harness mode runs each discovered class in an isolated child process by default. The in-memory `IMeasurementObserver` callback cannot cross the process boundary, so OTLP export is the cross-process channel: instrument the child, point its exporter at a collector, and live telemetry crosses the process boundary cleanly.

### Env-var forwarding

`ChildProcessLauncher` forwards the following environment variables from parent to every spawned child:

| Env var | Purpose |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP exporter endpoint |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP transport (`grpc` or `http/protobuf`) |
| `OTEL_EXPORTER_OTLP_HEADERS` | OTLP exporter headers (e.g. auth) |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | OTLP export timeout |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes (passed through) |
| `OTEL_SERVICE_NAME` | Service name (passed through) |
| `NBENCHMARK_OTEL_ENDPOINT` | NBenchmark-specific endpoint mirror (see `--otlp-endpoint` CLI flag) |

When `NBENCHMARK_OTEL_ENDPOINT` is set and `OTEL_EXPORTER_OTLP_ENDPOINT` is not, the launcher mirrors it into `OTEL_EXPORTER_OTLP_ENDPOINT` so an SDK wired only against the standard variable picks it up without extra configuration.

### `--otlp-endpoint` CLI flag

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
```

The harness mirrors this into `OTEL_EXPORTER_OTLP_ENDPOINT` before spawning isolated children, so children stream to the same collector as the parent. When the user has already set `OTEL_EXPORTER_OTLP_ENDPOINT` explicitly, the CLI flag does not override it.

### Observer forwarding

When `--observer <name>` is supplied, the parent forwards the observer names to isolated children via the `IsolatedRunRequest.ObserverNames` field. The child resolves each name through `ObserverRegistry` (populated identically by `[ModuleInitializer]` self-registration in the child's fresh process), so the same observers fire in the child as in the parent. Programmatic observers added via `WithObserver(IMeasurementObserver)` are live objects and cannot cross a process boundary; only registry-resolvable names are forwarded.

### Topology

```
In-process / local dev:
  AdaptiveLoop -> Observer shim -> Embedded web host -> React SPA in browser

Isolated / CI:
  Child process -> OTLP -> Collector
  Host process  -> OTLP -> Collector
  Collector -> Grafana / Jaeger / Honeycomb
```

In-process and isolated runs look identical to the dashboard: both are OTLP producers.

## See also

- `docs/reference/observers.md` - the `IMeasurementObserver` interface and event types.
- `docs/reference/cli.md` - the `--otlp-endpoint` CLI flag.
- `docs/statistics/diagnostics.md` - runtime diagnostics counters (GC, heap, exceptions, CPU).
