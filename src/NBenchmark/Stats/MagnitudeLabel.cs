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
    Negligible,
    Small,
    Medium,
    Large,
}

internal static class MagnitudeLabelExtensions
{
    /// <summary>
    ///     Romano, J., Kromrey, J. D., Coraggio, J., &amp; Skowronek, J. (2006).
    ///     Exploring methods for evaluating group differences on the NSSE and other surveys:
    ///     Are the t-test and Cohen's d the best options? AERA Conference.
    /// </summary>
    public static MagnitudeLabel Classify(double absDelta) => absDelta switch
    {
        < 0.147 => MagnitudeLabel.Negligible,
        < 0.33 => MagnitudeLabel.Small,
        < 0.474 => MagnitudeLabel.Medium,
        _ => MagnitudeLabel.Large,
    };

    public static string ToShortString(this MagnitudeLabel label) => label switch
    {
        MagnitudeLabel.Negligible => "neg",
        MagnitudeLabel.Small => "small",
        MagnitudeLabel.Medium => "med",
        MagnitudeLabel.Large => "large",
        _ => "neg",
    };
}
