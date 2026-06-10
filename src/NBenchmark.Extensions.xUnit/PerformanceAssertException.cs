namespace NBenchmark.Extensions.xUnit;

public sealed class PerformanceAssertException : Exception
{
    public PerformanceAssertException(string message)
        : base(message)
    {
    }
}
