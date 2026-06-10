namespace NBenchmark;

public enum OutlierMode
{
    None,
    RemoveTop5Percent,
    RemoveTopAndBottom5Percent,
    IqrFence,
}
