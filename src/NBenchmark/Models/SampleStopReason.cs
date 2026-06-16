namespace NBenchmark;

/// <summary>Why the adaptive measurement phase stopped, reported on <see cref="AutoTuneDiagnostic" />.</summary>
public enum SampleStopReason
{
    /// <summary>The confidence-interval half-width target (<see cref="AutoTuneOptions.CiTarget" />) was met.</summary>
    CiTargetMet = 0,

    /// <summary>The sample ceiling (<see cref="AutoTuneOptions.MaxSamples" />) was reached before the CI target.</summary>
    MaxCeiling = 1,

    /// <summary>Measurement ran a user-pinned exact count (<see cref="MeasurementOptions.Iterations" />).</summary>
    ExplicitCount = 2,

    /// <summary>The per-benchmark wall-clock cap (<see cref="AutoTuneOptions.MaxTuningTime" />) ended measurement early.</summary>
    WallClockCap = 3,
}
