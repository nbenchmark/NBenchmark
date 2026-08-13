using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Workers;

/// <summary>
///     Round-trips a <see cref="BigInteger" /> as its decimal digit string, registered on
///     <see cref="StateTransfer.SerializerOptions" />.
/// </summary>
/// <remarks>
///     Verified rather than assumed, because the failure is not an exception. With no converter of its
///     own, System.Text.Json serializes a <see cref="BigInteger" />'s public <i>properties</i> -
///     <c>IsZero</c>, <c>IsOne</c>, <c>Sign</c> and the like - rather than its value, and reads a
///     default straight back with nothing to say the value never crossed. Both directions live in one
///     converter for the same reason every fix on the wire does: an encoder and a decoder that
///     disagree would not fail, they would produce a different number.
/// </remarks>
internal sealed class BigIntegerConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => BigInteger.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
