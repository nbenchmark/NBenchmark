namespace NBenchmark;

/// <summary>
///     The base type for every exception NBenchmark raises deliberately.
///     <para>
///         Catch this to distinguish a failure the library refused - an unusable configuration, a
///         benchmark that could not be isolated, a worker that would not start - from an
///         <see cref="InvalidOperationException" /> thrown by the code under measurement. Argument
///         validation on options and builder methods still throws
///         <see cref="ArgumentOutOfRangeException" /> or <see cref="ArgumentException" />: those
///         report a bad call, not a refused run.
///     </para>
/// </summary>
public class BenchmarkException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public BenchmarkException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message" />.</summary>
    public BenchmarkException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message" /> and an underlying cause.</summary>
    public BenchmarkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Thrown when the way a benchmark or suite is configured cannot produce a measurement: a
///     <c>[BenchmarkPlan]</c> that is not static, parameter values that do not match the
///     parameterized bodies, a baseline name that no benchmark carries, a delegate shape the engine
///     cannot measure.
///     <para>
///         The failure is in the benchmark definition, so it is deterministic: the same program
///         fails the same way on every run until the definition changes.
///     </para>
/// </summary>
public sealed class BenchmarkConfigurationException : BenchmarkException
{
    /// <summary>Creates the exception with <paramref name="message" />.</summary>
    public BenchmarkConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message" /> and an underlying cause.</summary>
    public BenchmarkConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Thrown when isolation is required and a benchmark could not be measured in a worker process.
///     <para>
///         <see cref="Status" /> names the reason and <see cref="Remedy" /> carries the fix as
///         data, so a wrapper (a test adapter, a CI reporter) can act on the refusal without
///         parsing <see cref="Exception.Message" />. Both are also spelled out in the message.
///         When a run is refused for several benchmarks at once, <see cref="Status" /> is the
///         first offender's and the message lists every one of them.
///     </para>
/// </summary>
public sealed class BenchmarkIsolationException : BenchmarkException
{
    /// <summary>Creates the exception for a refusal described by <paramref name="status" />.</summary>
    public BenchmarkIsolationException(string message, IsolationStatus status)
        : base(message)
    {
        Status = status;
        Remedy = status.ToRemedy();
    }

    /// <summary>Why the measurement did not happen in a worker process.</summary>
    public IsolationStatus Status { get; }

    /// <summary>
    ///     What to change so the benchmark can be isolated, or <c>null</c> when the status has no
    ///     remedy (the user asked for the host process, or the run was refused for mixed reasons).
    /// </summary>
    public string? Remedy { get; }
}

/// <summary>
///     Thrown when a run that was correctly configured could not be carried out: a worker died
///     mid-run, a protocol frame exceeded the transport ceiling, a runtime the suite asked for is
///     not installed.
/// </summary>
/// <remarks>
///     A benchmark body that throws does not raise this. The body's exception is captured onto the
///     result (<see cref="BenchmarkResult.Errored" /> and
///     <see cref="BenchmarkResult.ErrorMessage" />) so the rest of the suite still runs and the
///     report still names the failure.
/// </remarks>
public sealed class BenchmarkExecutionException : BenchmarkException
{
    /// <summary>Creates the exception with <paramref name="message" />.</summary>
    public BenchmarkExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message" /> and an underlying cause.</summary>
    public BenchmarkExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Thrown when a worker process cannot be started or cannot be trusted. Callers treat this as
///     "fall back and say why", never as a reason to report a measurement.
/// </summary>
public sealed class WorkerStartException : BenchmarkException
{
    /// <summary>Creates the exception with <paramref name="message" />.</summary>
    public WorkerStartException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message" /> and an underlying cause.</summary>
    public WorkerStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
