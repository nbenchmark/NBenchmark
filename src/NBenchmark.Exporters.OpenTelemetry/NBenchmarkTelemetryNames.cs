namespace NBenchmark.Exporters.OpenTelemetry;

/// <summary>
///     The names this exporter subscribes to.
/// </summary>
/// <remarks>
///     Deliberately literals rather than a reference to the engine's own instrument definitions:
///     <c>NBenchmarkDiagnostics</c> is internal, and a subscriber only ever needs the strings. It
///     also keeps the coupling honest - these names are a published contract of the engine, and any
///     OpenTelemetry SDK subscribes to them the same way, whether or not this package exists.
/// </remarks>
internal static class NBenchmarkTelemetryNames
{
    /// <summary>The <c>ActivitySource</c> the engine raises spans from.</summary>
    internal const string Source = "NBenchmark";

    /// <summary>The <c>Meter</c> the engine records instruments on.</summary>
    internal const string Meter = "NBenchmark";

    /// <summary>Per-op sample duration, in nanoseconds.</summary>
    internal const string SampleDuration = "nbenchmark.sample.duration";

    /// <summary>Per-op allocation delta, in bytes.</summary>
    internal const string AllocationBytesPerOp = "nbenchmark.alloc.bytes_per_op";
}
