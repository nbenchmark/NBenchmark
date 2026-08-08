namespace NBenchmark.Workers;

/// <summary>
///     Where one parameter of a benchmark body gets its value, in the process that measures.
/// </summary>
/// <remarks>
///     <para>
///         Exactly one of two things: a <see cref="Value" /> encoded by
///         <see cref="TestArgumentCodec" />, or a <see cref="Recipe" /> the worker invokes. The
///         distinction is the one the whole isolation design turns on - a value that can be sent is
///         sent, and a value that cannot has its <i>instructions</i> sent instead.
///     </para>
///     <para>
///         One list of these, aligned with the body's parameters, replaced two separate slots on
///         <see cref="BodyRef" />: a list of encoded values and a single prepared-state factory,
///         documented as mutually exclusive. They were exclusive because nothing could express the
///         combinations in between, and four of the state gaps were exactly those combinations - a
///         second prepared value, a prepare delegate taking arguments of its own, a parameter sweep
///         whose values are too complex to encode, and a sweep mixing the two. Per-slot, all four are
///         the same shape and none needs a new wire field.
///     </para>
///     <para>
///         A recipe's own arguments ride on its <see cref="AddressedFactory.Body" />, which is a
///         <see cref="BodyRef" /> and therefore carries its own <see cref="BodyRef.Arguments" />. So
///         <c>prepare: (int size) =&gt; Build(size)</c> needs nothing here beyond what already
///         exists: the size is a value in the recipe's slot list, one level down.
///     </para>
/// </remarks>
internal sealed record ArgumentSource
{
    /// <summary>An encoded constant. Non-null exactly when <see cref="Recipe" /> is null.</summary>
    public TestArgumentPayload? Value { get; init; }

    /// <summary>
    ///     A factory to invoke once in the measuring process, before warmup, whose result becomes this
    ///     parameter's value. Non-null exactly when <see cref="Value" /> is null.
    /// </summary>
    public AddressedFactory? Recipe { get; init; }

    /// <summary>Whether this names exactly one source, or says what is wrong with it.</summary>
    /// <remarks>
    ///     Checked on receipt rather than left to hold by construction, for the reason
    ///     <see cref="AddressedFactory.IsWellFormed" /> gives: the coordinator's factories set one
    ///     field and only one, but the worker is reading a frame off a pipe, and a slot carrying two
    ///     claims about one parameter must be refused rather than resolved by whichever branch happens
    ///     to be tested first.
    /// </remarks>
    public bool IsWellFormed(out string? problem)
    {
        problem = null;

        if (Value is not null && Recipe is not null)
        {
            problem = "carries both an encoded value and a factory, which are two different claims "
                      + "about the same parameter.";

            return false;
        }

        if (Value is null && Recipe is null)
        {
            problem = "carries neither an encoded value nor a factory, so there is nothing to call "
                      + "the body with.";

            return false;
        }

        return true;
    }

    public static ArgumentSource FromValue(TestArgumentPayload value) => new() { Value = value };

    public static ArgumentSource FromRecipe(AddressedFactory recipe) => new() { Recipe = recipe };
}
