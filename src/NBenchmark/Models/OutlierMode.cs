namespace NBenchmark;

public enum OutlierMode
{
    None,
    RemoveTop5Percent,
    RemoveTop5PercentAndBottom5Percent,
    IqrFence,
}