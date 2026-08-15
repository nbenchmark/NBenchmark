namespace NBenchmark.Exporters.OpenTelemetry;

/// <summary>
///     Configuration for the OTLP exporter.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything here has to survive a process boundary.</b> A benchmark is measured in an
///         <c>nbworker</c> child that never runs the harness's entry point, so it never sees a
///         configuration callback - it has only its environment block. Every transport setting below
///         therefore maps onto an OTel-standard environment variable, which
///         <c>MeasurementBudget.ApplyTelemetryEnvironment</c> already forwards to each worker.
///     </para>
///     <para>
///         That constraint is why there is no <c>Action&lt;TracerProviderBuilder&gt;</c> escape
///         hatch. A callback would apply in the harness and be silently absent in the process that
///         does the measuring, which is worse than not offering it: the resulting telemetry would be
///         shaped one way in an <c>--in-process</c> run and another way in an isolated one, with
///         nothing to indicate why.
///     </para>
///     <para>
///         Instrument shaping - <see cref="DurationBucketBoundariesNs" /> and
///         <see cref="AllocationBucketBoundaries" /> - is exempt from the same problem for the
///         opposite reason: it is not configuration crossing the boundary, it is the same package
///         code running on both sides and applying the same defaults. Changing them from the harness
///         only changes the harness, so they are documented as defaults rather than as per-run knobs.
///     </para>
/// </remarks>
public sealed class OpenTelemetryOptions
{
    /// <summary>
    ///     The OTLP endpoint, e.g. <c>http://localhost:4317</c>. Maps to
    ///     <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.
    ///     <para>
    ///         Leaving this unset is how you turn the exporter off: with no endpoint from here, from
    ///         <c>--otlp-endpoint</c>, or from the environment, nothing is built and no connection is
    ///         opened. Referencing the package is not on its own a request to export.
    ///     </para>
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    ///     <c>grpc</c> (the default, port 4317) or <c>http/protobuf</c> (port 4318). Maps to
    ///     <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    ///     Exporter headers as a comma-separated <c>key=value</c> list, typically an auth token.
    ///     Maps to <c>OTEL_EXPORTER_OTLP_HEADERS</c>.
    /// </summary>
    public string? Headers { get; set; }

    /// <summary>Export timeout in milliseconds. Maps to <c>OTEL_EXPORTER_OTLP_TIMEOUT</c>.</summary>
    public int? TimeoutMilliseconds { get; set; }

    /// <summary>
    ///     The service name every span and metric is stamped with. Maps to
    ///     <c>OTEL_SERVICE_NAME</c>; defaults to <see cref="DefaultServiceName" />.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    ///     Extra resource attributes as a comma-separated <c>key=value</c> list. Maps to
    ///     <c>OTEL_RESOURCE_ATTRIBUTES</c>, which NBenchmark also reads when stamping its own run
    ///     attributes onto the suite span.
    /// </summary>
    public string? ResourceAttributes { get; set; }

    /// <summary>
    ///     How often metrics are exported.
    /// </summary>
    /// <remarks>
    ///     One second, against an SDK default of sixty. A run is over in seconds and a worker process
    ///     lives for one or two, so at the default a worker would ship one window or none, and the
    ///     observable gauges - CI half-width, running mean, sample count - would never be read while
    ///     there was anything to read.
    /// </remarks>
    public int MetricExportIntervalMilliseconds { get; set; } = 1_000;

    /// <summary>Whether to export traces. On by default.</summary>
    public bool EnableTraces { get; set; } = true;

    /// <summary>Whether to export metrics. On by default.</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    ///     Histogram bucket boundaries for <c>nbenchmark.sample.duration</c>, in nanoseconds.
    /// </summary>
    /// <remarks>
    ///     Roughly logarithmic from 1 ns to 5 ms. The SDK's default boundaries stop at 10,000, which
    ///     puts every benchmark slower than 10 µs in the overflow bucket and makes a percentile over
    ///     them meaningless - and per-op durations legitimately span four orders of magnitude within
    ///     a single suite.
    /// </remarks>
    public double[] DurationBucketBoundariesNs { get; set; } =
    [
        1, 2, 5, 10, 25, 50, 100, 250, 500,
        1_000, 2_500, 5_000, 10_000, 25_000, 50_000, 100_000,
        250_000, 500_000, 1_000_000, 5_000_000
    ];

    /// <summary>
    ///     Histogram bucket boundaries for <c>nbenchmark.alloc.bytes_per_op</c>, in bytes.
    /// </summary>
    /// <remarks>
    ///     Dense at the bottom because the interesting question is usually whether a body allocates
    ///     at all, and the first few buckets separate "nothing" from one small object.
    /// </remarks>
    public double[] AllocationBucketBoundaries { get; set; } =
        [0, 8, 16, 24, 32, 48, 64, 96, 128, 256, 512, 1_024, 4_096, 16_384, 65_536];

    /// <summary>The service name used when none is configured.</summary>
    public const string DefaultServiceName = "nbenchmark";
}
