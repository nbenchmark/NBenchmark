using Xunit;

namespace NBenchmark.Exporters.OpenTelemetry.Tests;

/// <summary>
///     Saves and restores every environment variable the exporter reads or writes.
/// </summary>
/// <remarks>
///     The environment is process-wide state and these tests both read and write it, so they cannot
///     run beside each other - hence <see cref="EnvironmentSerializedCollection" />. Restoring on
///     dispose keeps a test from leaking an endpoint into the next one, which would otherwise turn
///     "declines with no endpoint" into a test that passes or fails on ordering.
/// </remarks>
internal sealed class EnvironmentScope : IDisposable
{
    private static readonly string[] Managed =
    [
        TelemetryEnvironment.EndpointVariable,
        TelemetryEnvironment.ProtocolVariable,
        TelemetryEnvironment.HeadersVariable,
        TelemetryEnvironment.TimeoutVariable,
        TelemetryEnvironment.ServiceNameVariable,
        TelemetryEnvironment.ResourceAttributesVariable,
        TelemetryEnvironment.NBenchmarkEndpointVariable,
    ];

    private readonly Dictionary<string, string?> _saved = [];

    private EnvironmentScope()
    {
        foreach (var name in Managed)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public static EnvironmentScope Clear() => new();

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

[CollectionDefinition(nameof(EnvironmentSerializedCollection), DisableParallelization = true)]
public sealed class EnvironmentSerializedCollection;
