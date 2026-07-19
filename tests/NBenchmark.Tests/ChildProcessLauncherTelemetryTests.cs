using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class ChildProcessLauncherTelemetryTests
{
    // The NBenchmark-specific endpoint env var.
    private const string NbenchmarkEndpointEnvVar = "NBENCHMARK_OTEL_ENDPOINT";

    // The OTel-standard env vars the launcher forwards from parent to child. Mirrored from
    // ChildProcessLauncher.OtelStandardEnvVars so the test stays close to the contract.
    private static readonly string[] OtelEnvVars =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_RESOURCE_ATTRIBUTES",
        "OTEL_SERVICE_NAME",
    ];

    private static readonly string[] ManagedEnvVars =
    [
        ..OtelEnvVars,
        NbenchmarkEndpointEnvVar,
        "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_EXPORTER_OTLP_TIMEOUT",
    ];

    [Fact]
    public void BuildStartInfo_Forwards_OtelExporterEndpoint_To_Child()
    {
        using var _ = WithEnv(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"));

        var psi = ChildProcessLauncher.BuildStartInfo();

        Assert.Equal("http://collector:4317", psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public void BuildStartInfo_Forwards_OtelResourceAttributes_To_Child()
    {
        using var _ = WithEnv(("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=ci"));

        var psi = ChildProcessLauncher.BuildStartInfo();

        Assert.Equal("deployment.environment=ci", psi.Environment["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void BuildStartInfo_Forwards_OtelServiceName_To_Child()
    {
        using var _ = WithEnv(("OTEL_SERVICE_NAME", "nbench-ci"));

        var psi = ChildProcessLauncher.BuildStartInfo();

        Assert.Equal("nbench-ci", psi.Environment["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public void BuildStartInfo_Mirrors_NBenchmark_Endpoint_Into_Otel_Standard_Var_When_Unset()
    {
        // The NBenchmark-specific endpoint is mirrored into the OTel-standard var so an SDK
        // wired only against OTEL_EXPORTER_OTLP_ENDPOINT still picks it up.
        using var _ = WithEnv((NbenchmarkEndpointEnvVar, "http://mirror-target:4318"));

        var psi = ChildProcessLauncher.BuildStartInfo();

        Assert.Equal("http://mirror-target:4318", psi.Environment[NbenchmarkEndpointEnvVar]);
        Assert.Equal("http://mirror-target:4318", psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public void BuildStartInfo_Does_Not_Overwrite_Explicit_OtelEndpoint_With_NBenchmark_Endpoint()
    {
        // When the user has set OTEL_EXPORTER_OTLP_ENDPOINT explicitly, the NBenchmark-specific
        // endpoint does not override it. The standard var wins so a user who configured the SDK
        // directly is not surprised.
        using var _ = WithEnv(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://explicit:4317"), (NbenchmarkEndpointEnvVar, "http://mirror:4318"));

        var psi = ChildProcessLauncher.BuildStartInfo();

        Assert.Equal("http://explicit:4317", psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public void BuildStartInfo_Caller_Supplied_Vars_Win_Over_Inherited_Otel_Vars()
    {
        using var _ = WithEnv(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://inherited:4317"));

        var psi = ChildProcessLauncher.BuildStartInfo(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://overridden:4318"));

        Assert.Equal("http://overridden:4318", psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public void BuildStartInfo_WithEntryAssembly_Forwards_Otel_Vars_To_Child()
    {
        using var _ = WithEnv(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://cross-runtime:4317"));

        var psi = ChildProcessLauncher.BuildStartInfo("/path/to/child.dll");

        Assert.Equal("http://cross-runtime:4317", psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        // The cross-runtime overload uses `dotnet exec <assembly>`.
        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal("exec", psi.ArgumentList[0]);
        Assert.Equal("/path/to/child.dll", psi.ArgumentList[1]);
    }

    [Fact]
    public void BuildStartInfo_Does_Not_Inject_Otel_Vars_When_None_Set_In_Parent()
    {
        using var _ = WithEnv();

        var psi = ChildProcessLauncher.BuildStartInfo();

        foreach (var name in OtelEnvVars)
        {
            Assert.False(psi.Environment.ContainsKey(name), $"{name} should not be set when the parent has no OTel env vars");
        }
    }

    private static IDisposable WithEnv(IEnumerable<(string Name, string? Value)> vars)
    {
        var saved = new Dictionary<string, string?>();

        foreach (var name in ManagedEnvVars)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
        }

        foreach (var (name, value) in vars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return new EnvScope(saved);
    }

    /// <summary>
    ///     Convenience overload for the common case where every value is non-null. Avoids the
    ///     nullable-tuple mismatch warnings at every call site.
    /// </summary>
    private static IDisposable WithEnv(params (string Name, string Value)[] vars)
        => WithEnv(vars.Select(v => (v.Name, (string?)v.Value)));

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _saved;

        public EnvScope(Dictionary<string, string?> saved)
        {
            _saved = saved;
        }

        public void Dispose()
        {
            foreach (var (name, value) in _saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
