namespace NBenchmark.Workers;

/// <summary>
///     A factory the caller wants invoked in whichever process measures, together with the values it
///     takes - the coordinator-side input that becomes an <see cref="ArgumentSource.Recipe" />.
/// </summary>
/// <remarks>
///     <para>
///         The delegate and its arguments travel together because they are one claim. A prepare
///         delegate is refused if it captures, and the whole point of letting it take parameters is to
///         give the user somewhere to put the value they would otherwise have captured:
///         <c>prepare: () =&gt; Build(size)</c> closes over <c>size</c> and can only be refused, while
///         <c>prepare: (int size) =&gt; Build(size)</c> paired with <c>1000</c> is a complete recipe
///         that the worker can follow on its own.
///     </para>
///     <para>
///         That refusal is the one people find most frustrating, because they reach it <i>after</i>
///         doing the rewrite the diagnostic asked for.
///     </para>
/// </remarks>
/// <param name="Factory">
///     The recipe. Addressed by the same rule as a body, so it must not capture.
/// </param>
/// <param name="Arguments">
///     Values for <paramref name="Factory" />'s own parameters, in declaration order. Empty for the
///     parameterless factories that are the common case.
/// </param>
internal sealed record StateRecipe(Delegate Factory, IReadOnlyList<object?> Arguments)
{
    public static StateRecipe For(Delegate factory) => new(factory, []);

    public static StateRecipe For(Delegate factory, params object?[] arguments) => new(factory, arguments);

    /// <summary>
    ///     The single-slot list a body taking one prepared value needs, or <c>null</c> when there is no
    ///     recipe at all.
    /// </summary>
    public static IReadOnlyList<StateRecipe?>? OneSlot(StateRecipe? recipe) => recipe is null ? null : [recipe];
}
