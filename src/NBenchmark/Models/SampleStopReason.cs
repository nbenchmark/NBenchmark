namespace NBenchmark;

/// <summary>Why the adaptive measurement phase stopped, reported on <see cref="AutoTuneDiagnostic" />.</summary>
public enum SampleStopReason
{
    /// <summary>The confidence-interval half-width target (<see cref="AutoTuneOptions.CiTarget" />) was met.</summary>
    CiTargetMet = 0,

    /// <summary>The sample ceiling (<see cref="AutoTuneOptions.MaxSamples" />) was reached before the CI target.</summary>
    MaxCeiling = 1,

    /// <summary>Measurement ran a user-pinned exact count (<see cref="MeasurementOptions.Samples" />).</summary>
    ExplicitCount = 2,

    /// <summary>The per-benchmark wall-clock cap (<see cref="AutoTuneOptions.MaxTuningTime" />) ended measurement early.</summary>
    WallClockCap = 3,

    /// <summary>
    ///     The grace ceiling (<see cref="AutoTuneOptions.MaxTuningTime" /> *
    ///     <see cref="AutoTuneOptions.CapGraceFactor" />) was reached while still below
    ///     <see cref="AutoTuneOptions.MinSamples" />. The error margin is unreliable.
    /// </summary>
    GraceCapExhausted = 4,

    /// <summary>
    ///     The measured stream was still drifting - its first and second halves disagreed by more than
    ///     <see cref="AutoTuneOptions.MeasurementDriftTolerance" /> - after
    ///     <see cref="AutoTuneOptions.MeasurementRestartLimit" /> restarts. The confidence interval
    ///     describes a moving target rather than a stable measurement, so the reported centre is not
    ///     reproducible even though the interval may look narrow.
    /// </summary>
    DriftUnresolved = 5,
}
