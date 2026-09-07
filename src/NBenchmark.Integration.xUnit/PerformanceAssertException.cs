namespace NBenchmark.Integration.xUnit;

/// <summary>Thrown by <see cref="PerformanceTestCase" /> when a performance gate fails.</summary>
public sealed class PerformanceAssertException : Exception
{
    /// <summary>Creates the exception with the gate's violation message.</summary>
    public PerformanceAssertException(string message)
        : base(message)
    {
    }
}
