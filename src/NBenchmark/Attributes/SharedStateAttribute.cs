namespace NBenchmark;

/// <summary>
///     Declares that a <c>[InstanceLifetime(InstanceLifetime.PerClass)]</c> class carries state
///     across its <c>[Benchmark]</c> methods <i>on purpose</i>, so the engine neither resolves the
///     lifetime down to <see cref="InstanceLifetime.PerMethod" /> nor warns about the dependence.
/// </summary>
/// <remarks>
///     <para>
///         This exists because <see cref="Lifecycle.IStateReset" /> was carrying two unrelated
///         meanings at once. Implementing it said "I reset between methods, so PerClass is safe" -
///         but the engine only ever checked that the interface was <i>present</i>, never that the
///         body did anything, so an empty <c>return Task.CompletedTask;</c> silenced both the
///         warning and the automatic lifetime resolution while changing nothing about the shared
///         state. The IDE quick fix shipped exactly that body, which made accepting it and not
///         editing it the fastest route to a contaminated run.
///     </para>
///     <para>
///         Split apart, the two claims are checkable: <see cref="Lifecycle.IStateReset" /> means "I
///         reset, call me between methods" and its body is expected to do something, while this
///         attribute means "the coupling is deliberate, leave it alone" and needs no body to lie
///         about. A benchmark measuring the second call into a warm cache is a legitimate thing to
///         want; it just has to be said rather than implied.
///     </para>
///     <para>
///         The dependence does not go away - a significance test over methods that share an instance
///         is still comparing samples that are not independent. This says the author knows.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SharedStateAttribute : Attribute
{
    /// <summary>
    ///     Whether the sharing is deliberate. Defaults to <c>true</c>, so the bare
    ///     <c>[SharedState]</c> form is the declaration; set it to <c>false</c> to park the
    ///     attribute on a class without suppressing anything.
    /// </summary>
    public bool Acknowledged { get; init; } = true;
}
