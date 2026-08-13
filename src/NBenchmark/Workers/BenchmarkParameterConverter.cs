using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Workers;

/// <summary>
///     Crosses a <see cref="BenchmarkParameter" /> as its name plus a formatted display string and
///     the original value's type name, rather than letting <see cref="BenchmarkParameter.Value" />'s
///     declared <c>object?</c> serialize generically. Registered on
///     <see cref="FrameChannel.SerializerOptions" />, so both directions of the worker/coordinator
///     frame protocol agree.
/// </summary>
/// <remarks>
///     <para>
///         An <c>object?</c>-declared member gives the reader no type to deserialize into, so the
///         default behaviour produces a bare <c>JsonElement</c> - and worse, a
///         <c>[BenchmarkCase(typeof(X))]</c> value is a <c>System.RuntimeType</c>, which
///         <c>System.Text.Json</c> refuses to serialize at all. This converter sidesteps both: it
///         never asks the serializer to handle <see cref="BenchmarkParameter.Value" /> as itself,
///         only the string <see cref="BenchmarkParameter.FormatValue" /> already knows how to build
///         from it while it is still the real object, in the worker, before anything is written.
///     </para>
///     <para>
///         <b>Write</b> runs in the worker with the real value in hand, so it is the one side that
///         can still call <see cref="BenchmarkParameter.FormatValue" /> and read
///         <c>Value.GetType()</c> faithfully. <b>Read</b> runs in the coordinator, which never needs
///         a live typed value back - <c>FormatValue</c> and <c>BenchmarkParameter.GetKey</c> are
///         display and grouping only - so it rebuilds <see cref="RemoteParameterValue" /> instead,
///         which already carries everything either one reads.
///     </para>
/// </remarks>
internal sealed class BenchmarkParameterConverter : JsonConverter<BenchmarkParameter>
{
    public override BenchmarkParameter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for a benchmark parameter.");

        string? name = null;
        string? display = null;
        string? typeName = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();

            reader.Read();

            switch (property)
            {
                case "Name":
                    name = reader.GetString();
                    break;

                case "Display":
                    display = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;

                case "ValueTypeName":
                    typeName = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
            }
        }

        if (name is null)
        {
            throw new JsonException(
                "A benchmark parameter frame is missing its name, so which parameter this was cannot be recovered.");
        }

        // display is null both for a genuinely null parameter value and for a malformed frame that
        // omitted the field - the two are indistinguishable here, and a null value is exactly the
        // shape a missing one would otherwise be mistaken for, so treating them alike is the honest
        // answer rather than a guess in either direction.
        object? value = display is not null ? new RemoteParameterValue(display, typeName ?? "?") : null;

        return new BenchmarkParameter(name, value);
    }

    public override void Write(Utf8JsonWriter writer, BenchmarkParameter value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", value.Name);

        switch (value.Value)
        {
            case null:
                writer.WriteNull("Display");
                break;

            // Already crossed once - a relay hop, or a result written and read back - so its
            // Display and TypeName are carried through rather than re-derived from *this* record's
            // own type, which would name RemoteParameterValue itself instead of what it stands in for.
            case RemoteParameterValue remote:
                writer.WriteString("Display", remote.Display);
                writer.WriteString("ValueTypeName", remote.TypeName);
                break;

            default:
                writer.WriteString("Display", BenchmarkParameter.FormatValue(value.Value));
                writer.WriteString("ValueTypeName", value.Value.GetType().FullName ?? value.Value.GetType().Name);
                break;
        }

        writer.WriteEndObject();
    }
}
