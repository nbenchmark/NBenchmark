using System.Globalization;
using System.Reflection;

namespace NBenchmark.Exporters.OpenTelemetry;

/// <summary>
///     Reads and writes the OTel-standard environment variables that carry exporter configuration
///     from the harness into every worker process.
/// </summary>
/// <remarks>
///     The environment is the only channel available. A worker is a fresh process that runs none of
///     the harness's code, and NBenchmark already forwards these variables into each one
///     (<c>MeasurementBudget.ApplyTelemetryEnvironment</c>) - so writing configuration here is what
///     makes <c>WithOpenTelemetry(...)</c> in the harness take effect in the process that measures.
/// </remarks>
internal static class TelemetryEnvironment
{
    internal const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";
    internal const string HeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";
    internal const string TimeoutVariable = "OTEL_EXPORTER_OTLP_TIMEOUT";
    internal const string ServiceNameVariable = "OTEL_SERVICE_NAME";
    internal const string ResourceAttributesVariable = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>NBenchmark's own mirror of <c>--otlp-endpoint</c>.</summary>
    internal const string NBenchmarkEndpointVariable = "NBENCHMARK_OTEL_ENDPOINT";

    /// <summary>
    ///     Whether this process is an <c>nbworker</c> child rather than the harness.
    /// </summary>
    internal static bool IsWorkerProcess { get; } = string.Equals(
        Assembly.GetEntryAssembly()?.GetName().Name,
        "nbworker",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Builds options from the environment. The endpoint resolves from the OTel-standard
    ///     variable first, then NBenchmark's <c>--otlp-endpoint</c> mirror - the same precedence the
    ///     engine applies when forwarding, so the harness and its workers cannot disagree.
    /// </summary>
    internal static OpenTelemetryOptions ReadOptions() => new()
    {
        Endpoint = Read(EndpointVariable) ?? Read(NBenchmarkEndpointVariable),
        Protocol = Read(ProtocolVariable),
        Headers = Read(HeadersVariable),
        TimeoutMilliseconds = int.TryParse(
            Read(TimeoutVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
            ? timeout
            : null,
        ServiceName = Read(ServiceNameVariable),
        ResourceAttributes = Read(ResourceAttributesVariable),
    };

    /// <summary>
    ///     Publishes options to the environment so workers inherit them.
    /// </summary>
    /// <remarks>
    ///     Set, not merged: an explicit <c>WithOpenTelemetry(o =&gt; o.Endpoint = ...)</c> is a
    ///     statement about this run and should win over an ambient variable, the same way an explicit
    ///     CLI flag does. Options left null are not written, so anything already configured in the
    ///     environment survives untouched.
    /// </remarks>
    internal static void PublishOptions(OpenTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Write(EndpointVariable, options.Endpoint);
        Write(ProtocolVariable, options.Protocol);
        Write(HeadersVariable, options.Headers);
        Write(TimeoutVariable, options.TimeoutMilliseconds?.ToString(CultureInfo.InvariantCulture));
        Write(ServiceNameVariable, options.ServiceName);
        Write(ResourceAttributesVariable, options.ResourceAttributes);

        // Mirrored so the engine's own forwarding sees an endpoint even when the user configured one
        // here rather than through --otlp-endpoint.
        Write(NBenchmarkEndpointVariable, options.Endpoint);
    }

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static void Write(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Environment.SetEnvironmentVariable(name, value);
    }
}
