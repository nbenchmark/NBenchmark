namespace NBenchmark;

/// <summary>
///     What happens when the adaptive measurement loop stops because it hit the wall-clock
///     tuning cap before reaching the confidence-interval target or a steady warmup state.
/// </summary>
public enum AutoTuneCapBehavior
{
    /// <summary>Emit a warning on the benchmark result when the cap is reached (default).</summary>
    Warn = 0,

    /// <summary>Treat a cap hit as a benchmark error so CI gates can fail reliably.</summary>
    Error = 1,
}
