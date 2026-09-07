namespace NBenchmark;

/// <summary>The outcome of comparing a benchmark against its baseline for statistical significance.</summary>
public enum SignificanceVerdict
{
    /// <summary>No comparison was made (e.g. no baseline, or significance testing disabled).</summary>
    NotTested,

    /// <summary>The difference from the baseline is statistically significant.</summary>
    Significant,

    /// <summary>The difference from the baseline is not statistically significant.</summary>
    NotSignificant,
}
