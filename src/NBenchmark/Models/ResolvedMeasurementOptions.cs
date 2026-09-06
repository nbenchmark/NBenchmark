namespace NBenchmark;

/// <summary>
///     The profile-derived settings of a <see cref="MeasurementOptions" />, resolved to the concrete
///     values a measurement will use.
/// </summary>
/// <remarks>
///     Produced by <see cref="MeasurementOptions.Resolve" />. A request says <c>null</c> for "follow
///     the profile"; this says what the profile made of it.
/// </remarks>
public sealed record ResolvedMeasurementOptions
{
    /// <inheritdoc cref="MeasurementOptions.ForceGcBeforeEachIteration" />
    public required bool ForceGcBeforeEachIteration { get; init; }

    /// <inheritdoc cref="MeasurementOptions.ForceGcBeforeMeasurement" />
    public required bool ForceGcBeforeMeasurement { get; init; }

    /// <inheritdoc cref="MeasurementOptions.ForceGcBetweenBenchmarks" />
    public required bool ForceGcBetweenBenchmarks { get; init; }

    /// <inheritdoc cref="MeasurementOptions.MeasureAllocations" />
    public required bool MeasureAllocations { get; init; }
}
