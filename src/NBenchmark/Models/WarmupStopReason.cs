namespace NBenchmark;

/// <summary>Why the adaptive warmup phase stopped, reported on <see cref="AutoTuneDiagnostic" />.</summary>
public enum WarmupStopReason
{
    /// <summary>The body reached a steady state (the plateau rule fired) at or above the warmup floor.</summary>
    Settled = 0,

    /// <summary>The warmup ceiling (<see cref="AutoTuneOptions.MaxWarmupSamples" />) was reached before settling.</summary>
    MaxCeiling = 1,

    /// <summary>Warmup ran a user-pinned exact count (<see cref="MeasurementOptions.WarmupSamples" />).</summary>
    ExplicitCount = 2,

    /// <summary>The per-benchmark wall-clock cap (<see cref="AutoTuneOptions.MaxTuningTime" />) ended warmup early.</summary>
    WallClockCap = 3,
}
