namespace NBenchmark.Stats;

internal static class EffectMetrics
{
    public const string CliffsDelta = "Cliff's δ";
}

/// <summary>
///     Qualitative magnitude labels for Cliff's delta, using the Romano (2006) thresholds:
///     <c>|delta| &lt; 0.147</c> negligible, <c>&lt; 0.33</c> small, <c>&lt; 0.474</c> medium,
///     <c>&gt;= 0.474</c> large.
/// </summary>
public enum MagnitudeLabel
{
    /// <summary>The groups overlap almost entirely: <c>|delta| &lt; 0.147</c>.</summary>
    Negligible,

    /// <summary>A small but consistent separation: <c>|delta| &lt; 0.33</c>.</summary>
    Small,

    /// <summary>A clear separation: <c>|delta| &lt; 0.474</c>.</summary>
    Medium,

    /// <summary>The groups barely overlap: <c>|delta| &gt;= 0.474</c>.</summary>
    Large,
}

/// <summary>Rendering helpers for <see cref="MagnitudeLabel" />.</summary>
public static class MagnitudeLabelExtensions
{
    /// <summary>
    ///     Classifies <paramref name="absDelta" /> - the absolute value of Cliff's delta - into a
    ///     qualitative band.
    ///     <para>
    ///     Romano, J., Kromrey, J. D., Coraggio, J., &amp; Skowronek, J. (2006).
    ///     Exploring methods for evaluating group differences on the NSSE and other surveys:
    ///     Are the t-test and Cohen's d the best options? AERA Conference.
    ///     </para>
    /// </summary>
    internal static MagnitudeLabel Classify(double absDelta) => absDelta switch
    {
        < 0.147 => MagnitudeLabel.Negligible,
        < 0.33 => MagnitudeLabel.Small,
        < 0.474 => MagnitudeLabel.Medium,
        _ => MagnitudeLabel.Large,
    };

    /// <summary>
    ///     The abbreviated label reports use in a narrow column: <c>neg</c>, <c>small</c>,
    ///     <c>med</c>, <c>large</c>.
    /// </summary>
    public static string ToShortString(this MagnitudeLabel label) => label switch
    {
        MagnitudeLabel.Negligible => "neg",
        MagnitudeLabel.Small => "small",
        MagnitudeLabel.Medium => "med",
        MagnitudeLabel.Large => "large",
        _ => "neg",
    };
}
