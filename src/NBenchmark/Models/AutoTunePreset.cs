namespace NBenchmark;

/// <summary>
///     The <c>--auto-tune</c> flag's parse target: the name of one of the
///     <see cref="AutoTuneOptions" /> presets.
/// </summary>
/// <remarks>
///     Internal on purpose. <see cref="AutoTuneOptions.Default" />, <see cref="AutoTuneOptions.Quick" />
///     and <see cref="AutoTuneOptions.Thorough" /> are the public spelling of the same three bundles,
///     and a public enum beside them meant two ways to say one thing - two <c>WithAutoTune</c>
///     overloads on every builder, a converter between them, and no answer in the API to which one
///     wins. The options record is the model; this only carries a command-line word to it.
/// </remarks>
internal enum AutoTunePreset
{
    /// <summary>The balanced default (<see cref="AutoTuneOptions.Default" />).</summary>
    Default = 0,

    /// <summary>Fewer samples and a looser CI target for fast feedback (<see cref="AutoTuneOptions.Quick" />).</summary>
    Quick = 1,

    /// <summary>More samples and a tighter CI target for publication-grade numbers (<see cref="AutoTuneOptions.Thorough" />).</summary>
    Thorough = 2,
}
