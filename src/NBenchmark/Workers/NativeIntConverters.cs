using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Workers;

/// <summary>
///     Round-trips <see cref="nint" /> and <see cref="nuint" /> as plain JSON integers, registered on
///     <see cref="StateTransfer.SerializerOptions" />.
/// </summary>
/// <remarks>
///     Both are <c>IsPrimitive</c>, so <see cref="TestArgumentCodec.IsSupported" /> accepts a captured
///     field of either type - and System.Text.Json refuses to serialize <see cref="IntPtr" /> or
///     <see cref="UIntPtr" /> at all, throwing <see cref="NotSupportedException" /> unconditionally. A
///     scalar capture reached that throw directly: refused at encode with the serializer's own message,
///     naming neither the acceptance that had already been promised nor a genuine reason to decline.
///     A captured <c>nint[]</c> never hit it, because a blittable primitive array takes the binary
///     path instead of this serializer - one spelling of "a native integer" crossed and the other did
///     not, for a difference the user did not write. Writing both as
///     an ordinary JSON number - <c>long</c> for <see cref="nint" />, <c>ulong</c> for
///     <see cref="nuint" />, both wide enough on every platform this runs on - sidesteps the
///     unsupported type entirely rather than working around its refusal.
/// </remarks>
internal sealed class NintConverter : JsonConverter<nint>
{
    public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (nint)reader.GetInt64();

    public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options)
        => writer.WriteNumberValue((long)value);
}

/// <inheritdoc cref="NintConverter" />
internal sealed class NuintConverter : JsonConverter<nuint>
{
    public override nuint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (nuint)reader.GetUInt64();

    public override void Write(Utf8JsonWriter writer, nuint value, JsonSerializerOptions options)
        => writer.WriteNumberValue((ulong)value);
}
