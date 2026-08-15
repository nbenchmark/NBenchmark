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

- `benchmark.suite` (root): created at `OnSuiteStarting`, tags include `nbenchmark.suite.name`, `nbenchmark.suite.benchmark_count`, `nbenchmark.profile`, `nbenchmark.runtime`, `nbenchmark.seed`, `nbenchmark.run_order`; stopped at `OnSuiteCompleted` with `nbenchmark.suite.result_count`.
- `nbenchmark.worker` (per measuring process): opened as a worker's session begins, parented to the coordinator's span through the `TRACEPARENT` it inherited; tagged `nbenchmark.worker.pid` plus the run's resource attributes. It is what keeps an isolated run in one trace instead of one trace per process, and it renders worker startup as the gap before the first phase. An `--in-process` run has no such process and no such span.
- `benchmark.run` (per-benchmark): created at `OnBenchmarkRunStarting`, tags include `nbenchmark.name`, `nbenchmark.class`, `nbenchmark.baseline`, `nbenchmark.parameter_set`; stopped at `OnBenchmarkRunCompleted` with `nbenchmark.result.median_ns`, `nbenchmark.result.mean_ns`, `nbenchmark.result.sample_count`, `nbenchmark.result.outliers_removed`.

### Phase spans

Each phase transition creates an Activity span named `nbenchmark.phase.<phase>` where `<phase>` is one of `jitter`, `calibration`, `warmup`, or `measurement`. Phase spans nest under their parent `benchmark.run` span. Tags include:

| Tag | Set on | Value |
| --- | --- | --- |
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
| --- | --- | --- | --- |
| `detector.switched` | `nbenchmark.phase.jitter` | The outlier detector auto-switched IQR -> MAD | `nbenchmark.from`, `nbenchmark.to`, `nbenchmark.jitter_metric` |
| `warmup.plateau_reached` | `nbenchmark.phase.warmup` | Warmup stopped because the body settled (plateau rule) | - |
| `measurement.ci_target_met` | `nbenchmark.phase.measurement` | Measurement stopped because the CI half-width target was met | `nbenchmark.achieved_ci_width`, `nbenchmark.ci_target` |
| `phase.cap_hit` | `nbenchmark.phase.warmup` / `nbenchmark.phase.measurement` | A phase ended early at the wall-clock tuning cap | - |

## Getting started with OpenTelemetry

Install the exporter package:

```bash
dotnet add package NBenchmark.Exporters.OpenTelemetry
```

Then point it at a collector - on the command line:

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
```

or in code:

```csharp
using NBenchmark.Exporters.OpenTelemetry;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithOpenTelemetry(o => o.Endpoint = "http://localhost:4317")
    .RunAsync();
```

That is the whole integration. The package builds and owns the OpenTelemetry SDK, subscribes it to the `NBenchmark` `Meter` and `ActivitySource`, applies histogram buckets suited to per-op durations, and flushes when the run ends - in the harness **and inside every isolated worker process**, which is the part that is difficult to do by hand (see [Cross-process streaming](#cross-process-streaming)).

With no endpoint configured - not on the command line, not in code, not in `OTEL_EXPORTER_OTLP_ENDPOINT` - the exporter declines to build and nothing connects. Referencing the package is not on its own a request to export.

`samples/Telemetry/` is a runnable version, with a Docker Compose file that starts Grafana, Prometheus and Tempo and a provisioned dashboard for the instruments on this page. See [Samples](../samples.md#telemetry---opentelemetry-export-to-grafana).

### Configuration

`WithOpenTelemetry(...)` accepts the transport settings, each of which maps onto the OTel-standard environment variable of the same meaning:

| Option | Environment variable |
| --- | --- |
| `Endpoint` | `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `Protocol` | `OTEL_EXPORTER_OTLP_PROTOCOL` |
| `Headers` | `OTEL_EXPORTER_OTLP_HEADERS` |
| `TimeoutMilliseconds` | `OTEL_EXPORTER_OTLP_TIMEOUT` |
| `ServiceName` | `OTEL_SERVICE_NAME` |
| `ResourceAttributes` | `OTEL_RESOURCE_ATTRIBUTES` |

The mapping is not incidental. A worker process runs none of your code, so the environment is the only channel that reaches it, and configuration that cannot be expressed as a variable cannot cross the boundary - which is why the package exposes no `Action<TracerProviderBuilder>`. An option that applied in the harness and silently vanished in the process doing the measuring would be worse than one that does not exist.

Instrument shaping - histogram bucket boundaries and the metric export interval - is exempt, because it is not configuration crossing the boundary: it is the same package code running on both sides, applying the same defaults.

### Subscribing without the package

The instrumentation is plain BCL and needs no package at all. A host that runs NBenchmark in-process can attach its own listener:

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

All NBenchmark instruments are picked up when a `Meter` or `ActivitySource` named `NBenchmark` is subscribed to. This is enough for an `--in-process` run, and it is what a `MeterListener`-based UI would do to avoid an OTLP round trip. It is *not* enough for an isolated run - the section below is why.

## Resource attributes

Every `benchmark.suite` span is stamped with resource attributes that identify the run across commit, branch, CI pipeline, and machine. A downstream backend (Grafana, Jaeger, Honeycomb) can join on these to render cross-commit trend lines and regression alarms without NBenchmark shipping its own storage layer.

The attributes are read once per process from environment variables and cached for the process lifetime.

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
| `nbenchmark.branch` | `GITHUB_HEAD_REF`, `CI_COMMIT_BRANCH`, `GIT_BRANCH` | `git rev-parse --abbrev-ref HEAD` (detached HEAD produces no branch attribute) |

CI-sourced values take precedence over the git CLI fallback. When no CI or git env vars are present and the git CLI is unavailable (or outside a repo), the commit and branch attributes are omitted.

### Host identification

| Attribute | Value |
| --- | --- |
| `nbenchmark.host.machine_name` | `Environment.MachineName` |
| `nbenchmark.host.os` | `windows`, `macos`, or `linux` |
| `nbenchmark.host.arch` | `arm64`, `x64`, `x86`, etc. |
| `nbenchmark.host.runtime` | `RuntimeInformation.FrameworkDescription` (e.g. `.NET 8.0.22`) |

### OpenTelemetry-standard env vars

`OTEL_RESOURCE_ATTRIBUTES` and `OTEL_SERVICE_NAME` are honored verbatim. `OTEL_RESOURCE_ATTRIBUTES` is parsed as a comma-separated `key=value` list (the OTel convention) and each pair is copied onto the span. `OTEL_SERVICE_NAME` is mapped to `service.name`. A user who has already configured these for the rest of their service does not need to repeat themselves.

## Cross-process streaming

Benchmarks are measured in a separate `nbworker` process by default. Your own `IMeasurementObserver` and `IBenchmarkProgress` instances still fire: the worker streams its phase and progress events back over its pipe and your process replays them into the live objects you registered, so no OTLP configuration is needed to observe an isolated run.

What OTLP adds is a channel to something *outside* both processes - a collector, a tracing backend, a dashboard. For that the worker needs its own exporter configuration, which it inherits from your process's environment.

### Building the SDK inside the worker

An SDK constructed in your `Main` is constructed in the wrong process. The phase spans and per-sample metrics are emitted by the measurement loop, so in an isolated run they are emitted inside the worker - and the worker never runs your entry point. It loads your assembly and invokes the benchmark methods directly. Only the root `benchmark.suite` span, which the coordinator owns, would reach your collector.

`NBenchmark.Exporters.OpenTelemetry` handles this, and it is the reason to prefer the package over wiring the SDK yourself. Three things have to line up, none of which are obvious:

1. **The exporter registers from a `[ModuleInitializer]`**, and the worker runs that initializer explicitly after loading the `NBenchmark.*` packages your benchmark assembly references. Loading is not sufficient on its own: a module initializer runs before the first *access* to something in the module, and nothing in a worker accesses these types by name.
2. **Configuration arrives as environment variables**, because a worker has no other channel - see [Configuration](#configuration).
3. **`System.Diagnostics.DiagnosticSource` is unified with the worker's default load context.** `ActivitySource` and `Meter` publish to static state inside that assembly; a second copy loaded from the target's output means the SDK subscribes to one registry while the engine publishes to the other, and the run exports nothing with no error anywhere. This is framework-dependent - under `net10.0` the shared framework supplies the version the SDK wants and the question never arises, while under `net8.0` and `net9.0` NuGet copies its own - so it is exactly the kind of defect that passes a single-TFM test matrix.

### Env-var forwarding

`MeasurementBudget.ApplyTelemetryEnvironment` writes the following environment variables into every worker's environment block before it starts:

| Env var | Purpose |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP exporter endpoint |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP transport (`grpc` or `http/protobuf`) |
| `OTEL_EXPORTER_OTLP_HEADERS` | OTLP exporter headers (e.g. auth) |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | OTLP export timeout |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes (passed through) |
| `OTEL_SERVICE_NAME` | Service name (passed through) |
| `NBENCHMARK_OTEL_ENDPOINT` | NBenchmark-specific endpoint mirror (see `--otlp-endpoint` CLI flag) |
| `TRACEPARENT` | W3C trace context of the coordinator's current span, so the worker's spans join the run's trace rather than rooting one of their own |

When `NBENCHMARK_OTEL_ENDPOINT` is set and `OTEL_EXPORTER_OTLP_ENDPOINT` is not, the mirror is applied so an SDK wired only against the standard variable picks it up without extra configuration.

### `--otlp-endpoint` CLI flag

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
```

The harness mirrors this into `OTEL_EXPORTER_OTLP_ENDPOINT` before spawning isolated workers, so workers stream to the same collector as the host. When the user has already set `OTEL_EXPORTER_OTLP_ENDPOINT` explicitly, the CLI flag does not override it.

### Observer forwarding

Auto-attached observers fire in a worker as well as in the coordinator: the worker loads the `NBenchmark.*` packages the target assembly references, runs their module initializers, and resolves `ObserverRegistry`'s auto-attach list once per session, disposing it when the session ends. That is the mechanism the OTLP exporter activates through.

Explicit `--observer <name>` selections are **not** forwarded to workers today - the name list does not cross the protocol - so a named observer fires in the coordinator only. Programmatic observers added via `WithObserver(IMeasurementObserver)` are live objects and cannot cross a process boundary at all; what crosses for those is the event stream, which the coordinator replays into your instance (see [Cross-process streaming](#cross-process-streaming) above).

### Topology

```text
In-process / local dev:
  AdaptiveLoop -> Observer shim -> Embedded web host -> React SPA in browser

Isolated / CI:
  Worker       -> OTLP -> Collector      (exporter built inside the worker)
  Host process -> OTLP -> Collector      (suite span, and the trace context the workers inherit)
  Collector -> Grafana / Jaeger / Honeycomb
```

In-process and isolated runs look identical to the dashboard: both are OTLP producers, and both produce a single trace.

## See also

- [Measurement Observer](./observers.md) - the `IMeasurementObserver` interface and event types.
- [CLI Reference](./cli.md) - the `--otlp-endpoint` CLI flag.
- [Diagnostics](../statistics/diagnostics.md) - runtime diagnostics counters (GC, heap, exceptions, CPU).
