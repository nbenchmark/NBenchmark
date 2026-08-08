using System.Globalization;

namespace NBenchmark.Workers;

/// <summary>
///     Converts test-case argument values to and from their wire form.
/// </summary>
/// <remarks>
///     <para>
///         Both directions live here so they cannot drift. An encoder and a decoder that disagree
///         would not fail - they would produce a <i>different argument</i>, and the benchmark would
///         measure a different call than the test declared while reporting the declared name.
///     </para>
///     <para>
///         The permitted set is deliberately small and closed. It is not an oversight that arbitrary
///         objects are unsupported: reconstructing one requires guessing at state the test framework
///         built, and a reconstruction that is usually right is worse than a refusal.
///     </para>
/// </remarks>
internal static class TestArgumentCodec
{
    /// <summary>Whether a value of this type can cross the boundary intact.</summary>
    public static bool IsSupported(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum || underlying.IsPrimitive)
            return true;

        return underlying == typeof(string)
               || underlying == typeof(decimal)
               || underlying == typeof(DateTime)
               || underlying == typeof(DateTimeOffset)
               || underlying == typeof(TimeSpan)
               || underlying == typeof(Guid);
    }

    /// <summary>
    ///     Encodes one argument against its <b>declared parameter type</b> rather than the runtime
    ///     type of the value.
    /// </summary>
    /// <remarks>
    ///     The declared type is what the decoder must produce, and the two can differ: a
    ///     <c>long</c> parameter given the literal <c>1</c> arrives here as a boxed <c>int</c>.
    ///     Encoding the runtime type would send <c>Int32</c> and bind the wrong overload on the far
    ///     side.
    /// </remarks>
    public static TestArgumentPayload Encode(Type parameterType, object? value)
    {
        ArgumentNullException.ThrowIfNull(parameterType);

        return new TestArgumentPayload
        {
            TypeName = parameterType.AssemblyQualifiedName ?? parameterType.FullName ?? parameterType.Name,
            Value = value switch
            {
                null => null,

                // The round-trip formats, named explicitly. The general IFormattable branch below
                // selects the "G" format, which for these two drops sub-second precision and
                // DateTimeKind entirely - and Decode already passes DateTimeStyles.RoundtripKind,
                // which only means anything if the encoder wrote "O". The two agreed on the type and
                // disagreed on the format, so a value encoded here decoded to a *different instant*:
                // 2024-03-05T13:45:30.1230000Z arrived as 2024-03-05T13:45:30.0000000 Unspecified.
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),

                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            },
        };
    }

    /// <summary>
    ///     Decodes one argument to <paramref name="parameterType" />, which the worker reads from the
    ///     resolved method rather than trusting the payload's own type name.
    /// </summary>
    public static object? Decode(TestArgumentPayload payload, Type parameterType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(parameterType);

        var underlying = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

        if (payload.Value is null)
        {
            if (underlying == parameterType && parameterType.IsValueType)
            {
                throw new InvalidOperationException(
                    $"A null argument cannot be bound to non-nullable parameter type '{parameterType.Name}'.");
            }

            return null;
        }

        if (underlying == typeof(string))
            return payload.Value;

        if (underlying.IsEnum)
            return Enum.Parse(underlying, payload.Value, ignoreCase: false);

        if (underlying == typeof(Guid))
            return Guid.Parse(payload.Value);

        if (underlying == typeof(DateTime))
            return DateTime.Parse(payload.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (underlying == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(payload.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (underlying == typeof(TimeSpan))
            return TimeSpan.Parse(payload.Value, CultureInfo.InvariantCulture);

        // Both are IsPrimitive, so IsSupported accepts them - and Convert.ChangeType does not, because
        // IntPtr's IConvertible implementation throws for a string source. The claim and the capability
        // disagreed: a sweep or an [InlineData] over a native integer passed every check the coordinator
        // makes and then faulted the group on arrival. Parsed explicitly instead, which is what the
        // acceptance was always promising.
        if (underlying == typeof(nint))
            return nint.Parse(payload.Value, CultureInfo.InvariantCulture);

        if (underlying == typeof(nuint))
            return nuint.Parse(payload.Value, CultureInfo.InvariantCulture);

        return Convert.ChangeType(payload.Value, underlying, CultureInfo.InvariantCulture);
    }
}
