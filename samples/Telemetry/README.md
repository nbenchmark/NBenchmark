# Telemetry sample - streaming a benchmark run to Grafana

NBenchmark emits `System.Diagnostics` instrumentation - a `Meter` and an `ActivitySource`, both
named `NBenchmark` - from the same points that feed `IMeasurementObserver`. The engine ships no
exporter, because the BCL has none: something has to serialise spans and metrics onto the wire.
`NBenchmark.Exporters.OpenTelemetry` is that something.

This sample points it at a Grafana stack in Docker. The run is isolated, the way harness mode
normally runs, and the telemetry from every `nbworker` child lands in one trace with the harness's.

## Run it

```bash
docker compose up -d          # Grafana + Prometheus + Tempo, one container
dotnet run -c Release         # runs the benchmarks and exports to it
open http://localhost:3000/d/nbenchmark-run
```

The image is large (~3 GB) and the stack takes about 30 seconds to come up the first time. There
are no volumes, so `docker compose down` discards the data along with the containers. Traces take
30-60 seconds to become searchable in Tempo after a run finishes - the dashboard's trace panel is
empty until then, which is Tempo's ingestion lag rather than a missing export.

## The whole integration

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<TelemetryBenchmarks>()
    .WithOpenTelemetry(o => o.Endpoint = "http://localhost:4317")
    .RunAsync();
```

Even that call is optional. Referencing the package and configuring an endpoint any other way -
`--otlp-endpoint http://localhost:4317`, or `OTEL_EXPORTER_OTLP_ENDPOINT` in the environment - is
enough on its own, because the exporter registers itself as an auto-attached observer. It is here
only to supply a local default so `dotnet run` works with no arguments.

With no endpoint configured anywhere, the exporter declines to build and nothing connects.
Referencing the package is not on its own a request to export.

## What you get

One trace per run, spanning every process involved. For the default four benchmarks over five
launches, that is 106 spans:

```text
benchmark.suite                          harness process
  └── nbenchmark.worker × 5              one per launch; the gap at the start is process startup
        └── benchmark.run × 4            one per benchmark measured in that worker
              ├── nbenchmark.phase.jitter        host jitter probe; may switch the outlier detector
              ├── nbenchmark.phase.calibration   resolves ops-per-sample (nbenchmark.resolved_k)
              ├── nbenchmark.phase.warmup        ends at the plateau, subject to the settle gates
              └── nbenchmark.phase.measurement   ends when the CI half-width target is met
```

`--in-process` collapses the `nbworker` layer into the harness. Worth running once for the
contrast, but not worth adopting: in-process measurement cannot apply the steady-state runtime
profile, and NBenchmark prints a prominent warning that the numbers are materially less
trustworthy.

Things to look for:

- **Span events explain why each phase ended.** `warmup.plateau_reached`,
  `measurement.ci_target_met` (tagged with the achieved width and the target), `phase.cap_hit`,
  and `detector.switched` render as markers on the row.
- **Warmup usually dwarfs measurement.** The 500 ms warmup time floor is the binding constraint for
  almost every body, while measurement stops as soon as the CI target is met - often in ~100 ms.
- **`SpanFormat` has by far the longest calibration span** and a large `nbenchmark.resolved_k`. The
  body is a few nanoseconds, so hundreds of invocations are batched into one timed sample.
- **Every span carries `nbenchmark.measured_in`**, `host` or `worker`, so a query can always tell
  which side of the process boundary produced it.

## How it reaches the worker

This is the part worth reading if you are wiring telemetry into your own harness, because it is
where a hand-rolled integration goes wrong.

The per-phase spans and per-sample metrics are emitted by the measurement loop, so in an isolated
run they are emitted **inside the worker** - and the worker never runs your entry point. It loads
your benchmark assembly and invokes the methods directly. An SDK built in `Main` is built in the
wrong process.

The package handles it in three parts, none of which you have to write:

1. **It registers from a `[ModuleInitializer]`**, and the worker runs that initializer explicitly
   after loading the packages your benchmark assembly references. Loading an assembly is not enough
   on its own - a module initializer runs before the first *access* to something in the module, and
   nothing in a worker accesses these types by name.
2. **Configuration travels as OTel-standard environment variables.** `WithOpenTelemetry(...)` runs
   only in the harness, so it writes `OTEL_EXPORTER_OTLP_ENDPOINT` and friends, which NBenchmark
   already forwards into every worker's environment block. This is why there is no
   `Action<TracerProviderBuilder>` escape hatch: a callback could not be honoured in the process
   that measures, and an option that silently applies on one side of the boundary is worse than one
   that does not exist.
3. **Trace context crosses the same way.** The engine writes the current span's id into
   `TRACEPARENT` when it launches a worker, and the worker opens its `nbenchmark.worker` span
   against it. Without that, a run produces one trace per process and the flame graph that makes
   the phase structure legible is exactly what is lost.

Flushing is deterministic on both sides: the harness disposes the exporter when the run ends, and
the worker disposes it when its session ends. A `ProcessExit` handler is a backstop, not the
primary path.

## Reading the metrics

The dashboard covers the main instruments; the full table is in
[docs/reference/bcl-instrumentation.md](../../docs/reference/bcl-instrumentation.md).

- `nbenchmark.sample.duration` (histogram, ns/op) - tagged `benchmark`, `phase`, and `warmup`, so
  the warmup distribution can be separated from the measured one.
- `nbenchmark.alloc.bytes_per_op` (histogram, B/op) - only `ConcatStrings` is non-zero.
- `nbenchmark.ci.relative_half_width` (gauge) - the convergence signal. It falls toward the ±2.5%
  target; where it stalls is where the measurement phase is spending its budget.
- `nbenchmark.outliers.removed`, `nbenchmark.jitter.detector_switches`, `nbenchmark.gc.gen0/1/2` -
  counters.

The package replaces the SDK's default histogram buckets, which stop at 10,000 - useless for a
per-op duration spanning four orders of magnitude - and drops the metric export interval from 60
seconds to 1, because a worker process does not live for 60 seconds.

### Why the dashboard aggregates over the range instead of rating per second

A worker lives for a second or two. At a 1-second export interval that is **two to four data points
per series**, after which the series is dead and the process is gone. `rate()` over series that
short is unreliable and, once the series ages past the lookback window, returns nothing at all -
measured here, the rate-based panels returned NaN for half the benchmarks minutes after a successful
run.

So the panels aggregate over the dashboard's time range instead:

```promql
histogram_quantile(0.95, sum by (benchmark, le) (
  last_over_time(nbenchmark_sample_duration_nanoseconds_per_op_bucket{phase="measurement"}[$__range])
))
```

`last_over_time` takes each dead series' final cumulative value from within the range and sums
across every process that contributed, so an isolated run's five launches are pooled into one
distribution. The same query is correct for an `--in-process` run, where there is one long-lived
series instead of twenty short ones. A benchmark run is a batch job, not a service, and this is what
querying a batch job looks like.

For the same reason the dashboard defaults to a **6-hour** window rather than the 15 minutes that
suits a live service: a run you did over lunch should still be on screen when you come back. Widen
the time picker further if you are comparing across a day.

### Names in Prometheus

The collector rewrites OTLP metric names on the way in - dots to underscores, unit appended - so the
names you query are not the names above:

| Instrument | Prometheus series |
| --- | --- |
| `nbenchmark.sample.duration` | `nbenchmark_sample_duration_nanoseconds_per_op_bucket` / `_count` / `_sum` |
| `nbenchmark.alloc.bytes_per_op` | `nbenchmark_alloc_bytes_per_op_B_per_op_bucket` / `_count` / `_sum` |
| `nbenchmark.ci.relative_half_width` | `nbenchmark_ci_relative_half_width_ratio` |
| `nbenchmark.jitter.metric` | `nbenchmark_jitter_metric_ratio` |
| `nbenchmark.sample.mean_per_op` | `nbenchmark_sample_mean_per_op_nanoseconds_per_op` |
| `nbenchmark.ops_per_second` | `nbenchmark_ops_per_second_per_second` |
| `nbenchmark.samples.count` | `nbenchmark_samples_count` |
| `nbenchmark.outliers.removed` | `nbenchmark_outliers_removed_samples_total` |
| `nbenchmark.gc.gen0` | `nbenchmark_gc_gen0_collections_total` |

To see what a run actually produced:

```bash
curl -s 'http://localhost:9090/api/v1/label/__name__/values' | tr ',' '\n' | grep nbenchmark
```

## Why this project multi-targets

`net8.0` and `net10.0`, and the `net8.0` half is a regression test rather than a demonstration.

`OpenTelemetry.Api` depends on `System.Diagnostics.DiagnosticSource` 10.0.0. Under `net10.0` the
shared framework supplies it and nothing is copied next to the app. Under `net8.0` NuGet copies its
own, and unless the worker's load context unifies that assembly with the default context, the SDK
inside a worker subscribes to a different listener registry than the one the engine publishes to -
and an isolated run exports nothing, with no error anywhere.

```bash
dotnet run -c Release -f net8.0     # the case that breaks if the unification regresses
dotnet run -c Release -f net10.0
```

Both should produce the same 106-span trace.

## Limits worth knowing

- **A killed worker exports what it had, not what it was about to send.** Flushing happens as the
  session ends; a worker terminated outright - Ctrl-C, a cancelled run - loses its last window.
- **Each worker is a separate `service.instance.id`.** Prometheus sees one short series per process
  per instrument, which is what the range aggregation above exists to handle.

## Pointing at your own collector

```bash
dotnet run -c Release -- --otlp-endpoint http://collector.internal:4317
```

Or configure it in code, which is the same thing by a different route:

```csharp
.WithOpenTelemetry(o =>
{
    o.Endpoint = "https://otlp.example.com";
    o.Protocol = "http/protobuf";
    o.Headers = "authorization=Bearer <token>";
    o.ServiceName = "checkout-benchmarks";
})
```

## See also

- [BCL Instrumentation](../../docs/reference/bcl-instrumentation.md) - the full instrument, span,
  and resource-attribute reference.
- [Measurement Observer](../../docs/reference/observers.md) - the in-process callback surface the
  same emit points feed.
