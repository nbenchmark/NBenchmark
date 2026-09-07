namespace NBenchmark;

/// <summary>One reported percentile and its computed value.</summary>
/// <param name="Percentile">The percentile in [0, 1] (e.g. 0.95 for P95).</param>
/// <param name="Value">The measured value at that percentile, in nanoseconds.</param>
public readonly record struct PercentileEntry(double Percentile, double Value);
