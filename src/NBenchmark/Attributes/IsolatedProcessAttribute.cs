namespace NBenchmark.Attributes;

/// <summary>
///     Forces a benchmark (or an entire benchmark class) into its own dedicated child
///     process - the finest isolation granularity.
///     <para>
///         Harness mode is isolated-by-default: each discovered class already runs in its
///         own clean-room child process, so a warmed-up thread pool, JIT artifacts, and
///         background GC pressure from one class never bleed into another. Within a class,
///         the benchmarks share that single child. Apply <c>[IsolatedProcess]</c> to a
///         method to split it out into its own child, separate from its class siblings;
///         apply it to a class to give every one of its benchmarks a private child.
///     </para>
///     <para>
///         Because the per-class default already isolates classes from one another, this
///         attribute is rarely needed. Applying it to a class multiplies the process count
///         - a class with <c>N</c> benchmarks then launches <c>N</c> child processes instead
///         of one - so prefer the method-level form unless every benchmark in the class
///         genuinely needs its own clean CLR.
///     </para>
///     <para>
///         To opt out of isolation entirely and run in the host process, use
///         <see cref="InProcessAttribute" /> or the global <c>--in-process</c> flag.
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IsolatedProcessAttribute : Attribute;
