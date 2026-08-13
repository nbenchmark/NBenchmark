using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Workers;

/// <summary>
///     Converters for the three ordered collection types <see cref="StateTransfer.IsFaithful" /> admits
///     that System.Text.Json cannot round-trip on its own - registered on
///     <see cref="StateTransfer.SerializerOptions" />, so both directions of the crossing use the same
///     code and cannot drift.
/// </summary>
/// <remarks>
///     <para>
///         <c>ReadOnlyCollection&lt;T&gt;</c> and <c>ArraySegment&lt;T&gt;</c> serialize to a plain JSON
///         array with no help - the failure is only on the way back, because neither exposes a
///         constructor the serializer's reflection-based collection support recognizes. Both fixes are
///         the same shape: read the array into a <c>List&lt;T&gt;</c>, then hand it to the constructor
///         that actually exists.
///     </para>
///     <para>
///         <c>Stack&lt;T&gt;</c> is different, and worse: it does not throw, it lies. Verified rather
///         than assumed, because the failure mode was not obvious from the docs - pushing <c>1, 2, 3</c>
///         serializes to <c>[3,2,1]</c> (top first, which is also enumeration order), and feeding that
///         array straight back into <c>new Stack&lt;T&gt;(IEnumerable&lt;T&gt;)</c> pushes <c>3</c>
///         first, so it ends up on the <b>bottom</b> - the rebuilt stack pops <c>1, 2, 3</c>, the exact
///         reverse of the one that was captured. Reversing the list before construction is what makes
///         the two agree.
///     </para>
/// </remarks>
internal static class OrderedCollectionConverters
{
    public static void Register(JsonSerializerOptions options)
    {
        options.Converters.Add(new ReadOnlyCollectionConverterFactory());
        options.Converters.Add(new ArraySegmentConverterFactory());
        options.Converters.Add(new StackConverterFactory());
    }

    private sealed class ReadOnlyCollectionConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ReadOnlyCollection<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlyCollectionConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    private sealed class ReadOnlyCollectionConverter<T> : JsonConverter<ReadOnlyCollection<T>>
    {
        public override ReadOnlyCollection<T> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(ReadElements<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, ReadOnlyCollection<T> value, JsonSerializerOptions options)
            => WriteElements(writer, value, options);
    }

    private sealed class ArraySegmentConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ArraySegment<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(ArraySegmentConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    private sealed class ArraySegmentConverter<T> : JsonConverter<ArraySegment<T>>
    {
        public override ArraySegment<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(ReadElements<T>(ref reader, options).ToArray());

        public override void Write(Utf8JsonWriter writer, ArraySegment<T> value, JsonSerializerOptions options)
            => WriteElements(writer, value, options);
    }

    private sealed class StackConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Stack<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(StackConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    private sealed class StackConverter<T> : JsonConverter<Stack<T>>
    {
        public override Stack<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // The array is top-first - the same order Write below produces, and the same order the
            // built-in converter would have used. Reversed before construction so the first element
            // pushed is the one that was at the bottom, leaving the original top on top again.
            var topFirst = ReadElements<T>(ref reader, options);

            topFirst.Reverse();

            return new Stack<T>(topFirst);
        }

        public override void Write(Utf8JsonWriter writer, Stack<T> value, JsonSerializerOptions options)
            => WriteElements(writer, value, options);
    }

    private static List<T> ReadElements<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array.");

        var items = new List<T>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            items.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);

        return items;
    }

    private static void WriteElements<T>(Utf8JsonWriter writer, IEnumerable<T> items, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var item in items)
            JsonSerializer.Serialize(writer, item, options);

        writer.WriteEndArray();
    }
}
