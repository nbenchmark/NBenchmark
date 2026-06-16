namespace NBenchmark;

/// <summary>
///     Named bundles of <see cref="AutoTuneOptions" /> that trade measurement time for
///     precision. Select one with <c>WithAutoTune(AutoTunePreset)</c> or the
///     <c>--auto-tune</c> CLI flag.
/// </summary>
public enum AutoTunePreset
{
    /// <summary>The balanced default (<see cref="AutoTuneOptions.Default" />).</summary>
    Default = 0,

    /// <summary>Fewer samples and a looser CI target for fast feedback (<see cref="AutoTuneOptions.Quick" />).</summary>
    Quick = 1,

    /// <summary>More samples and a tighter CI target for publication-grade numbers (<see cref="AutoTuneOptions.Thorough" />).</summary>
    Thorough = 2,
}
