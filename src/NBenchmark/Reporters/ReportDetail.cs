namespace NBenchmark.Reporters;

/// <summary>The level of detail a reporter includes in its output.</summary>
public enum ReportDetail
{
    /// <summary>The minimal view: name and headline timing only.</summary>
    Simple,

    /// <summary>The default view: timing, spread, and significance.</summary>
    Standard,

    /// <summary>The full view: every statistic and diagnostic the result carries.</summary>
    Advanced,
}
