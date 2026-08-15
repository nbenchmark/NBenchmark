using System.Diagnostics;
using System.Runtime.CompilerServices;
using NBenchmark.Observers;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NBenchmark.Exporters.OpenTelemetry;

/// <summary>
///     Subscribes an OpenTelemetry SDK to NBenchmark's <c>Meter</c> and <c>ActivitySource</c> and
///     exports what they emit over OTLP.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is an observer.</b> It never uses an observer callback - the engine's BCL
///         instrumentation emits on its own and the SDK built here is already listening. What the
///         registry provides, and what this needs, is a lifetime: something the harness constructs
///         once per run and disposes when the run ends, in the process that is doing the measuring.
///         <c>Dispose</c> is the flush. Inventing a parallel registry to express that would have
///         duplicated registration, naming, dedup and disposal for no gain.
///     </para>
///     <para>
///         <b>Why a module initializer.</b> Registration has to happen without anyone calling into
///         this assembly, because in an <c>nbworker</c> process nobody does: the worker loads the
///         benchmark assembly and invokes its methods, and never runs the harness's entry point. The
///         worker loads this package because the target references it, and loading is what runs the
///         initializer below. Same mechanism <c>NBenchmark.Reporters.Console</c> uses to register the
///         <c>console</c> reporter.
///     </para>
///     <para>
///         <b>Why it can decline.</b> The factory returns <see cref="NullMeasurementObserver" /> when
///         no endpoint is configured, which the registry drops. Referencing a package should not on
///         its own open a network connection, and a benchmark run with no collector configured is the
///         normal case.
///     </para>
/// </remarks>
public sealed class OpenTelemetryExporter : IMeasurementObserver
{
    /// <summary>The registry name, for <c>--observer otlp</c>.</summary>
    public const string ObserverName = "otlp";

    private readonly TracerProvider? _tracerProvider;
    private readonly MeterProvider? _meterProvider;
    private int _disposed;

    /// <summary>
    ///     Builds the providers described by <paramref name="options" />. Prefer
    ///     <c>WithOpenTelemetry</c> - this is public for the rare host that owns its own lifetime.
    /// </summary>
    public OpenTelemetryExporter(OpenTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(options.ServiceName ?? OpenTelemetryOptions.DefaultServiceName)
            .AddAttributes([
                // Which side of the process boundary produced a data point. The engine stamps the
                // run's identity (commit, branch, host) onto its spans already; this is the one
                // thing it cannot know, because it is a fact about the process rather than the run.
                new KeyValuePair<string, object>(
                    "nbenchmark.measured_in",
                    TelemetryEnvironment.IsWorkerProcess ? "worker" : "host")
            ]);

        if (options.EnableTraces)
        {
            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(NBenchmarkTelemetryNames.Source)
                .AddOtlpExporter()
                .Build();
        }

        if (options.EnableMetrics)
        {
            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(NBenchmarkTelemetryNames.Meter)
                .AddOtlpExporter((_, reader) =>
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                        options.MetricExportIntervalMilliseconds)
                .AddView(NBenchmarkTelemetryNames.SampleDuration, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = options.DurationBucketBoundariesNs,
                })
                .AddView(NBenchmarkTelemetryNames.AllocationBytesPerOp, new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = options.AllocationBucketBoundaries,
                })
                .Build();
        }

        // A backstop, not the primary flush. The harness disposes this observer when the run ends
        // and the worker disposes it when its session ends, both of which are deterministic; this
        // covers the paths that reach neither, such as an unhandled exception on the way out.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <inheritdoc />
    public string? Name => ObserverName;

    /// <summary>
    ///     Flushes both providers and tears them down. Idempotent, because it is reached from the
    ///     run's own disposal and from the process-exit backstop.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        // Traces first: the worker-session span closes immediately before this runs, and a flush
        // that shipped metrics but lost that span would leave the trace missing its root.
        Flush(_tracerProvider);
        Flush(_meterProvider);

        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
    }

    // The engine's own instrumentation carries the measurement; nothing here needs a callback, and
    // an empty body keeps the hot path free of a virtual call that would deliver events nobody reads.
    /// <inheritdoc />
    public void OnPhase(in MeasurementPhaseEvent phase) { }

    /// <inheritdoc />
    public void OnSample(in SampleEvent sample) { }

    /// <inheritdoc />
    public void OnDetector(in DetectorStateEvent detector) { }

    /// <inheritdoc />
    public void OnResult(BenchmarkResult result) { }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    private static void Flush(BaseProvider? provider)
    {
        const int FlushTimeoutMs = 10_000;

        try
        {
            switch (provider)
            {
                case TracerProvider tracer:
                    tracer.ForceFlush(FlushTimeoutMs);
                    break;

                case MeterProvider meter:
                    meter.ForceFlush(FlushTimeoutMs);
                    break;
            }
        }
        catch (Exception ex)
        {
            // An unreachable collector must not fail the run. The numbers are already measured and
            // reported; losing the export is a telemetry problem, not a measurement one.
            Trace.TraceWarning("NBenchmark: flushing the OTLP exporter failed: {0}", ex.Message);
        }
    }

    [ModuleInitializer]
    internal static void Register() => ObserverRegistry.RegisterAutoAttach(
        ObserverName,
        "Exports measurement telemetry over OTLP to an OpenTelemetry collector.",
        Create);

    /// <summary>
    ///     Builds an exporter if an endpoint is configured anywhere, and declines otherwise by
    ///     returning the null observer - which <c>ObserverRegistry.CreateAutoAttachedObservers</c>
    ///     filters out, so a declined exporter costs nothing at all.
    /// </summary>
    internal static IMeasurementObserver Create()
    {
        var options = TelemetryEnvironment.ReadOptions();

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            return NullMeasurementObserver.Instance;

        try
        {
            return new OpenTelemetryExporter(options);
        }
        catch (Exception ex)
        {
            // Same reasoning as the flush: a benchmark run that cannot export is still a benchmark
            // run. The registry traces and skips a throwing factory, but saying which endpoint was
            // being configured is the difference between a usable diagnostic and a shrug.
            Trace.TraceWarning(
                "NBenchmark: the OTLP exporter could not be built for endpoint '{0}': {1}",
                options.Endpoint, ex.Message);

            return NullMeasurementObserver.Instance;
        }
    }
}
