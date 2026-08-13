using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark;

public sealed record BenchmarkParameter(string Name, object? Value)
{
    /// <summary>
    ///     Formats a single parameter value for display. Numeric and other
    ///     <see cref="IFormattable" /> values use the invariant culture so output is stable across
    ///     locales (and consistent with <see cref="GetKey" />). Enums render as their member name.
    /// </summary>
    public static string FormatValue(object? value) => value switch
    {
        null => "null",
        RemoteParameterValue remote => remote.Display,
        string s => s,
        char c => c.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    /// <summary>The type name <see cref="GetKey" /> uses for a real (non-remote) value.</summary>
    private static string TypeNameOf(object value) => value.GetType().FullName ?? value.GetType().Name;

    /// <summary>Formats a single parameter as <c>name=value</c>.</summary>
    public static string FormatPart(BenchmarkParameter parameter)
        => $"{parameter.Name}={FormatValue(parameter.Value)}";

    /// <summary>
    ///     Joins a parameter set into a <c>name=value, ...</c> label, without surrounding
    ///     parentheses. Returns an empty string for an empty set.
    /// </summary>
    public static string FormatLabel(IReadOnlyList<BenchmarkParameter> paramSet)
        => string.Join(", ", paramSet.Select(FormatPart));

    /// <summary>
    ///     Builds the canonical display name for a parameterised benchmark:
    ///     <c>baseName(name=value, ...)</c>. Returns <paramref name="baseName" /> unchanged when the
    ///     parameter set is empty.
    /// </summary>
    public static string FormatDisplayName(string baseName, IReadOnlyList<BenchmarkParameter> paramSet)
        => paramSet.Count == 0 ? baseName : $"{baseName}({FormatLabel(paramSet)})";

    public static string GetKey(IReadOnlyList<BenchmarkParameter> paramSet)
    {
        if (paramSet.Count == 0)
            return "";

        var builder = new StringBuilder();

        for (var i = 0; i < paramSet.Count; i++)
        {
            if (i > 0)
                builder.Append('\u001F');

            var parameter = paramSet[i];
            AppendPart(builder, parameter.Name);
            builder.Append('=');

            if (parameter.Value is null)
            {
                AppendPart(builder, "<null>");
                builder.Append('@');
                AppendPart(builder, "<null>");
                continue;
            }

            string valueText;
            string typeName;

            // A remote value already carries both - computed in the worker, where the real value
            // was still in hand, rather than reconstructed from a JSON projection of it that has
            // forgotten what type it started out as. Keying an isolated row on
            // "System.Text.Json.JsonElement" - the same string for every parameter regardless of
            // what it actually held - is what let an in-process row and its isolated counterpart
            // land in different groups for one shared table.
            if (parameter.Value is RemoteParameterValue remote)
            {
                valueText = remote.Display;
                typeName = remote.TypeName;
            }
            else
            {
                valueText = Convert.ToString(parameter.Value, CultureInfo.InvariantCulture)
                            ?? parameter.Value.ToString()
                            ?? "?";
                typeName = TypeNameOf(parameter.Value);
            }

            AppendPart(builder, valueText);
            builder.Append('@');
            AppendPart(builder, typeName);
        }

        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, string text)
    {
        builder.Append(text.Length);
        builder.Append(':');
        builder.Append(text);
    }
}

/// <summary>
///     A parameter value as it arrives from an isolated worker, rather than the real value a body
///     was actually invoked with.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="BenchmarkResult.ParameterSet" /> crosses the worker/coordinator wire inside the
///         completed-benchmark frame, and <see cref="BenchmarkParameter.Value" /> is declared
///         <c>object?</c> - so on arrival <c>System.Text.Json</c> has no target type to deserialize
///         into and produces a bare <c>JsonElement</c>, the same one regardless of whether the real
///         value was an <c>int</c> or a <c>DayOfWeek</c>. Two things followed from that: an enum
///         rendered as its underlying number instead of its member name, and <see cref="GetKey" />'s
///         type component read <c>System.Text.Json.JsonElement</c> for every isolated parameter,
///         which is what let an in-process row and its isolated counterpart land in different
///         groups for a table that should have compared them.
///     </para>
///     <para>
///         Fixed by formatting the value in the worker, where it is still the real object, rather
///         than trying to reformat it in the coordinator, where it no longer is. Nothing here
///         reconstructs a live typed value on arrival - <see cref="BenchmarkParameter.FormatValue" />
///         and <see cref="GetKey" /> are display and grouping only, so a formatted string plus the
///         original type's name is everything either one needs.
///     </para>
///     <para>
///         This also removes the crash a <c>[BenchmarkCase(typeof(X))]</c> value used to cause: a
///         <c>System.RuntimeType</c> instance is one <c>System.Text.Json</c> refuses outright
///         (<c>NotSupportedException</c>, at the frame write), while <c>FormatValue</c> already
///         renders it - the same way it always has for an in-process row - with no serializer
///         involved at all.
///     </para>
/// </remarks>
[JsonConverter(typeof(RemoteParameterValueConverter))]
internal sealed record RemoteParameterValue(string Display, string TypeName)
{
    public override string ToString() => Display;
}

/// <summary>
///     The fallback for a <see cref="RemoteParameterValue" /> reaching any serializer other than the
///     worker/coordinator frame protocol - a user-facing report, say. Written as the plain display
///     string, since a consumer with no reason to know this type exists should see the same thing
///     <see cref="BenchmarkParameter.FormatValue" /> would show, not an object shaped like this
///     record's own fields.
/// </summary>
/// <remarks>
///     The frame protocol itself never reaches this: it addresses <see cref="BenchmarkParameter" />
///     as a whole and writes <see cref="RemoteParameterValue.Display" /> and
///     <see cref="RemoteParameterValue.TypeName" /> directly, so this converter's <see cref="Read" />
///     is never exercised on that path either - nothing else has a reason to parse this shape back
///     into a live object, so it refuses rather than guessing at one.
/// </remarks>
internal sealed class RemoteParameterValueConverter : JsonConverter<RemoteParameterValue>
{
    public override RemoteParameterValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException(
            $"{nameof(RemoteParameterValue)} is written for display, never read back, outside the "
            + "frame protocol's own converter.");

    public override void Write(Utf8JsonWriter writer, RemoteParameterValue value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Display);
}
