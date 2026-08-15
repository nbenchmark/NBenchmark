using System.Diagnostics;
using NBenchmark.Observers;
using Xunit;

namespace NBenchmark.Exporters.OpenTelemetry.Tests;

/// <summary>
///     Covers the two decisions the exporter makes before it does anything expensive: whether to
///     build at all, and what it publishes for worker processes to inherit.
/// </summary>
[Collection(nameof(EnvironmentSerializedCollection))]
public class OpenTelemetryExporterTests
{
    [Fact]
    public void Create_Declines_When_No_Endpoint_Is_Configured()
    {
        using var _ = EnvironmentScope.Clear();

        // Referencing the package must not on its own open a connection. The registry drops the
        // null observer, so declining here is what makes the auto-attach free for the many runs
        // that have no collector.
        Assert.Same(NullMeasurementObserver.Instance, OpenTelemetryExporter.Create());
    }

    [Fact]
    public void Create_Builds_An_Exporter_When_An_Endpoint_Is_Configured()
    {
        using var _ = EnvironmentScope.Clear();

        Environment.SetEnvironmentVariable(
            TelemetryEnvironment.EndpointVariable, "http://localhost:4317");

        using var observer = OpenTelemetryExporter.Create();

        Assert.IsType<OpenTelemetryExporter>(observer);
        Assert.Equal(OpenTelemetryExporter.ObserverName, observer.Name);
    }

    [Fact]
    public void Create_Accepts_The_NBenchmark_Endpoint_Mirror()
    {
        using var _ = EnvironmentScope.Clear();

        // --otlp-endpoint reaches a worker as NBENCHMARK_OTEL_ENDPOINT even when the standard
        // variable is unset, so the exporter has to accept the mirror as an endpoint in its own
        // right rather than waiting for the engine to have copied it across.
        Environment.SetEnvironmentVariable(
            TelemetryEnvironment.NBenchmarkEndpointVariable, "http://localhost:4317");

        using var observer = OpenTelemetryExporter.Create();

        Assert.IsType<OpenTelemetryExporter>(observer);
    }

    [Fact]
    public void The_Exporter_Attaches_A_Listener_To_The_NBenchmark_Source()
    {
        using var _ = EnvironmentScope.Clear();
        using var source = new ActivitySource("NBenchmark");

        Assert.False(source.HasListeners());

        using (var exporter = new OpenTelemetryExporter(new OpenTelemetryOptions
               {
                   Endpoint = "http://localhost:4317",
                   ServiceName = "nbenchmark-tests",
               }))
        {
            // The whole contract of the package in one assertion: after construction, the spans the
            // engine raises are being recorded. Everything else - endpoints, buckets, flushing -
            // only matters if this holds.
            Assert.True(source.HasListeners());

            using var activity = source.StartActivity("probe");

            Assert.NotNull(activity);
        }
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        using var _ = EnvironmentScope.Clear();

        var exporter = new OpenTelemetryExporter(new OpenTelemetryOptions
        {
            Endpoint = "http://localhost:4317",
        });

        // Reached from the run's own disposal and again from the process-exit backstop, so a second
        // call has to be harmless rather than a double-flush or a throw on the way out of a process.
        exporter.Dispose();
        exporter.Dispose();
    }

    [Fact]
    public void Registration_Puts_The_Exporter_In_The_Auto_Attach_List()
    {
        // The module initializer has run by the time any test in this assembly executes, because
        // touching OpenTelemetryExporter above is exactly the access that triggers it.
        Assert.Contains(
            ObserverRegistry.AutoAttached,
            o => string.Equals(o.Name, OpenTelemetryExporter.ObserverName, StringComparison.OrdinalIgnoreCase));
    }
}
