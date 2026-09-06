namespace NBenchmark.Attributes;

/// <summary>
///     Declares how a benchmark (or an entire benchmark class) is isolated, overriding the run-wide
///     setting.
/// </summary>
/// <remarks>
///     <para>
///         Harness mode runs each discovered class in its own worker process by default, and the
///         benchmarks within a class share that one worker.
///         <see cref="NBenchmark.Isolation.Off" /> keeps the annotated benchmarks in the host process
///         instead - faster (no process spawn) and simpler to debug, at the cost of inheriting the
///         host's warmed-up runtime state. <see cref="NBenchmark.Isolation.Required" /> goes the other
///         way and splits the annotated benchmark into a worker of its own, separate from its class
///         siblings.
///     </para>
///     <para>
///         Because the per-class default already isolates classes from one another,
///         <c>[Isolation(Isolation.Required)]</c> is rarely needed. Applying it to a class multiplies
///         the process count - a class with <c>N</c> benchmarks then launches <c>N</c> worker
///         processes instead of one - so prefer the method-level form unless every benchmark in the
///         class genuinely needs its own clean CLR.
///     </para>
///     <para>
///         A method-level attribute wins over a class-level one. The global <c>--in-process</c> flag
///         overrides everything.
///     </para>
/// </remarks>
/// <param name="isolation">The isolation to apply to the annotated benchmark or class.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IsolationAttribute(Isolation isolation) : Attribute
{
    /// <summary>The isolation this benchmark or class asks for.</summary>
    public Isolation Isolation { get; } = isolation;
}
