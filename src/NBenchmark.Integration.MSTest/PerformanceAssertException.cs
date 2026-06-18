namespace NBenchmark.Integration.MSTest;

[Serializable]
public sealed class PerformanceAssertException : AssertFailedException
{
    public PerformanceAssertException(string message)
        : base(message)
    {
    }

    public PerformanceAssertException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
