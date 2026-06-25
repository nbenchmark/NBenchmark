namespace NBenchmark.Attributes;

/// <summary>
///     Opts a benchmark (or an entire benchmark class) out of Harness mode's
///     isolated-by-default execution, running it in the host process instead of a child.
///     <para>
///         Harness mode runs each discovered class in its own child process by default for a
///         clean-room reading. In-process execution is faster (no process spawn) and
///         simpler to debug, at the cost of inheriting the host's warmed-up runtime state.
///         Apply <c>[InProcess]</c> to a method to run just that benchmark in the host
///         process, or to a class to keep all of its benchmarks in-process.
///     </para>
///     <para>
///         A method-level attribute wins over a class-level one, and a method-level
///         <see cref="IsolatedProcessAttribute" /> wins over a class-level
///         <c>[InProcess]</c>, so a mostly in-process class can still force one benchmark
///         into its own child. The global <c>--in-process</c> flag overrides everything.
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class InProcessAttribute : Attribute;
