namespace NBenchmark.Exporters.OpenTelemetry;

/// <summary>
///     Turns on OTLP export for a harness or a suite.
/// </summary>
/// <remarks>
///     <para>
///         Calling this is optional. Referencing the package and configuring an endpoint any other
///         way - <c>--otlp-endpoint</c>, or <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> in the environment -
///         is enough on its own, because the exporter self-registers as an auto-attached observer.
///         What these methods add is somewhere to put the configuration in code.
///     </para>
///     <para>
///         They deliberately return the harness or suite unchanged. The configuration lands in the
///         environment rather than on the object, because the object does not travel to the process
///         that measures and the environment does - see <see cref="OpenTelemetryOptions" />.
///     </para>
/// </remarks>
public static class OpenTelemetryExtensions
{
    /// <summary>
    ///     Configures OTLP export for this harness and every worker it starts.
    /// </summary>
    /// <example>
    ///     <code>
    ///     await BenchmarkHarness.Create(args)
    ///         .AddFromAssembly&lt;MyBenchmarks&gt;()
    ///         .WithOpenTelemetry(o => o.Endpoint = "http://localhost:4317")
    ///         .RunAsync();
    ///     </code>
    /// </example>
    public static BenchmarkHarness WithOpenTelemetry(
        this BenchmarkHarness harness,
        Action<OpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(harness);

        Configure(configure);

        return harness;
    }

    /// <summary>
    ///     Configures OTLP export for this suite and every worker it starts.
    /// </summary>
    public static BenchmarkSuite WithOpenTelemetry(
        this BenchmarkSuite suite,
        Action<OpenTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(suite);

        Configure(configure);

        return suite;
    }

    private static void Configure(Action<OpenTelemetryOptions>? configure)
    {
        // Seeded from the environment so a caller who sets only the endpoint does not silently
        // discard an OTEL_EXPORTER_OTLP_HEADERS the surrounding deployment configured.
        var options = TelemetryEnvironment.ReadOptions();

        configure?.Invoke(options);

        TelemetryEnvironment.PublishOptions(options);
    }
}
