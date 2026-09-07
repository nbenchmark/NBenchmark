namespace NBenchmark;

/// <summary>The built-in strategies for trimming outlier samples before statistics are computed.</summary>
public enum OutlierMode
{
    /// <summary>No trimming; every sample is kept.</summary>
    None,

    /// <summary>Removes the top 5% of samples by value.</summary>
    RemoveTop5Percent,

    /// <summary>Removes the top and bottom 5% of samples by value.</summary>
    RemoveTopAndBottom5Percent,

    /// <summary>Removes samples outside the interquartile-range fence.</summary>
    IqrFence,

    /// <summary>Removes samples outside a median-absolute-deviation fence.</summary>
    MedianAbsoluteDeviation,
}
