namespace NBenchmark.Integration.MSTest;

/// <summary>Thrown by <see cref="PerformanceTestMethodAttribute" /> when a performance gate fails.</summary>
[Serializable]
public sealed class PerformanceAssertException : AssertFailedException
{
    /// <summary>Creates the exception with the gate's violation message.</summary>
    public PerformanceAssertException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the gate's violation message and an inner exception.</summary>
    public PerformanceAssertException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
