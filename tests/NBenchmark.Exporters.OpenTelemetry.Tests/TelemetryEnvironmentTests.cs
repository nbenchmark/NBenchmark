using Xunit;

namespace NBenchmark.Exporters.OpenTelemetry.Tests;

/// <summary>
///     Covers the configuration hand-off. These variables are the only channel between
///     <c>WithOpenTelemetry(...)</c> in the harness and the exporter built inside a worker, so what
///     they carry is the whole of what a worker can be told.
/// </summary>
[Collection(nameof(EnvironmentSerializedCollection))]
public class TelemetryEnvironmentTests
{
    [Fact]
    public void PublishOptions_Writes_The_Standard_Variables()
    {
        using var _ = EnvironmentScope.Clear();

        TelemetryEnvironment.PublishOptions(new OpenTelemetryOptions
        {
            Endpoint = "http://collector:4317",
            Protocol = "grpc",
            Headers = "authorization=Bearer token",
            TimeoutMilliseconds = 5_000,
            ServiceName = "nbench-ci",
            ResourceAttributes = "deployment.environment=ci",
        });

        Assert.Equal("http://collector:4317", Environment.GetEnvironmentVariable(TelemetryEnvironment.EndpointVariable));
        Assert.Equal("grpc", Environment.GetEnvironmentVariable(TelemetryEnvironment.ProtocolVariable));
        Assert.Equal("authorization=Bearer token", Environment.GetEnvironmentVariable(TelemetryEnvironment.HeadersVariable));
        Assert.Equal("5000", Environment.GetEnvironmentVariable(TelemetryEnvironment.TimeoutVariable));
        Assert.Equal("nbench-ci", Environment.GetEnvironmentVariable(TelemetryEnvironment.ServiceNameVariable));
        Assert.Equal("deployment.environment=ci", Environment.GetEnvironmentVariable(TelemetryEnvironment.ResourceAttributesVariable));
    }

    [Fact]
    public void PublishOptions_Mirrors_The_Endpoint_Into_The_NBenchmark_Variable()
    {
        using var _ = EnvironmentScope.Clear();

        // The engine forwards its own variable as well as the standard one; writing both means an
        // endpoint configured in code reaches a worker by whichever route the engine takes.
        TelemetryEnvironment.PublishOptions(new OpenTelemetryOptions { Endpoint = "http://collector:4317" });

        Assert.Equal(
            "http://collector:4317",
            Environment.GetEnvironmentVariable(TelemetryEnvironment.NBenchmarkEndpointVariable));
    }

    [Fact]
    public void PublishOptions_Leaves_Unset_Options_Alone()
    {
        using var _ = EnvironmentScope.Clear();

        Environment.SetEnvironmentVariable(TelemetryEnvironment.HeadersVariable, "authorization=from-deployment");

        // Configuring only the endpoint must not wipe headers the surrounding deployment set. A
        // caller who names one thing is configuring one thing.
        TelemetryEnvironment.PublishOptions(new OpenTelemetryOptions { Endpoint = "http://collector:4317" });

        Assert.Equal(
            "authorization=from-deployment",
            Environment.GetEnvironmentVariable(TelemetryEnvironment.HeadersVariable));
    }

    [Fact]
    public void ReadOptions_Prefers_The_Standard_Endpoint_Over_The_Mirror()
    {
        using var _ = EnvironmentScope.Clear();

        Environment.SetEnvironmentVariable(TelemetryEnvironment.EndpointVariable, "http://explicit:4317");
        Environment.SetEnvironmentVariable(TelemetryEnvironment.NBenchmarkEndpointVariable, "http://mirror:4318");

        // Same precedence the engine applies when forwarding, so the harness and its workers cannot
        // end up exporting to two different collectors.
        Assert.Equal("http://explicit:4317", TelemetryEnvironment.ReadOptions().Endpoint);
    }

    [Fact]
    public void ReadOptions_Round_Trips_What_PublishOptions_Wrote()
    {
        using var _ = EnvironmentScope.Clear();

        TelemetryEnvironment.PublishOptions(new OpenTelemetryOptions
        {
            Endpoint = "http://collector:4318",
            Protocol = "http/protobuf",
            TimeoutMilliseconds = 2_500,
            ServiceName = "round-trip",
        });

        var read = TelemetryEnvironment.ReadOptions();

        Assert.Equal("http://collector:4318", read.Endpoint);
        Assert.Equal("http/protobuf", read.Protocol);
        Assert.Equal(2_500, read.TimeoutMilliseconds);
        Assert.Equal("round-trip", read.ServiceName);
    }
}
