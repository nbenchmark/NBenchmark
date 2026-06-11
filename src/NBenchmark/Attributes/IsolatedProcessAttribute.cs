namespace NBenchmark.Attributes;

/// <summary>
///     Marks a benchmark (or an entire benchmark class) to run in a dedicated child
///     process instead of in the host process.
///     <para>
///         In-process execution is fast and convenient, but the host's runtime state -
///         a warmed-up thread pool, JIT artifacts, and background GC pressure - bleeds
///         into the measurement. That is usually fine for relative comparisons, but for
///         a clean-room reading (no inherited thread-pool hill-climbing, a fresh CLR) the
///         host spins up a child process for the decorated benchmark, runs it there, and
///         reads the metrics back. Applying the attribute to a class isolates every
///         benchmark it declares.
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IsolatedProcessAttribute : Attribute;
