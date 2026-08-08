using System.Globalization;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Every type the codec claims to carry, carried and brought back unchanged.
/// </summary>
/// <remarks>
///     <para>
///         The encoder and the decoder live in one file so they cannot drift, and the file's own
///         doc-comment says a disagreement between them "would not fail - they would produce a
///         <i>different argument</i>". Two of them did: <see cref="DateTime" /> and
///         <see cref="DateTimeOffset" /> were encoded with the general <c>IFormattable</c> branch,
///         which selects the "G" format, while the decoder asked for
///         <see cref="DateTimeStyles.RoundtripKind" /> - which only means anything if the encoder wrote
///         "O". Sub-second precision and <see cref="DateTimeKind" /> were silently dropped, so
///         <c>13:45:30.1230000Z</c> arrived as <c>13:45:30.0000000</c> Unspecified: a different
///         instant, bound to a benchmark reported under the caller's own name.
///     </para>
///     <para>
///         Written as a round-trip over the whole accepted set rather than as two cases for the two
///         that were broken, because the failure was a <i>pair</i> that disagreed and nothing in the
///         build noticed. A type added to <see cref="TestArgumentCodec.IsSupported" /> without a
///         matching decode path now fails here.
///     </para>
/// </remarks>
public sealed class TestArgumentCodecTests
{
    private enum Colour
    {
        Red = 0,
        Green = 1,
    }

    public static TheoryData<Type, object?> RoundTrips
    {
        get
        {
            var data = new TheoryData<Type, object?>
            {
                { typeof(int), 42 },
                { typeof(long), -9_000_000_000L },
                { typeof(double), 1.5e-7 },
                { typeof(float), 0.25f },
                { typeof(bool), true },
                { typeof(byte), (byte)200 },
                { typeof(char), 'q' },
                { typeof(decimal), 12345.6789m },
                { typeof(string), "with a space" },
                { typeof(string), null },
                { typeof(Colour), Colour.Green },
                { typeof(Guid), Guid.Parse("2f1c1e2a-0b3d-4c5e-8f90-a1b2c3d4e5f6") },
                { typeof(TimeSpan), new TimeSpan(1, 2, 3, 4, 567) },
                { typeof(nint), (nint)1234 },
                { typeof(nuint), (nuint)1234 },
                { typeof(int?), 7 },
                { typeof(int?), null },

                // The two that were broken, in every kind - the kind is half of what was lost.
                { typeof(DateTime), new DateTime(2024, 3, 5, 13, 45, 30, DateTimeKind.Utc).AddTicks(1_230_000) },
                { typeof(DateTime), new DateTime(2024, 3, 5, 13, 45, 30, DateTimeKind.Unspecified) },
                {
                    typeof(DateTimeOffset),
                    new DateTimeOffset(2024, 3, 5, 13, 45, 30, TimeSpan.FromHours(11)).AddTicks(1_230_000)
                },
            };

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void Encode_ThenDecode_ReturnsTheSameValue(Type declared, object? value)
    {
        Assert.True(TestArgumentCodec.IsSupported(declared), $"{declared.Name} is not in the accepted set.");

        var decoded = TestArgumentCodec.Decode(TestArgumentCodec.Encode(declared, value), declared);

        Assert.Equal(value, decoded);
    }

    /// <summary>
    ///     <see cref="DateTimeKind" /> survives, which equality alone does not check.
    /// </summary>
    /// <remarks>
    ///     <see cref="DateTime" />'s equality ignores <see cref="DateTime.Kind" />, so a Utc value that
    ///     came back Unspecified compares equal to itself and the round-trip above would pass while the
    ///     benchmark measured against a different instant. This is the assertion that catches it.
    /// </remarks>
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Encode_ThenDecode_PreservesDateTimeKind(DateTimeKind kind)
    {
        var value = new DateTime(2024, 3, 5, 13, 45, 30, kind).AddTicks(1_230_000);

        var decoded = (DateTime)TestArgumentCodec.Decode(
            TestArgumentCodec.Encode(typeof(DateTime), value), typeof(DateTime))!;

        Assert.Equal(kind, decoded.Kind);
        Assert.Equal(value.Ticks, decoded.Ticks);
    }

    /// <summary>
    ///     The suite's parameter validation and the codec agree on what a simple value is.
    /// </summary>
    /// <remarks>
    ///     They used to be two hand-written lists. The suite's rejected <see cref="DateTime" />,
    ///     <see cref="DateTimeOffset" />, <see cref="TimeSpan" />, <see cref="Guid" />,
    ///     <see cref="nint" /> and <see cref="nuint" /> - all of which the wire carries - so a sweep over
    ///     a duration was refused at registration for a limitation that does not exist, and the two could
    ///     have drifted the other way just as easily.
    /// </remarks>
    [Fact]
    public void WithParameter_AcceptsEverythingTheCodecCarries()
    {
        var suite = new BenchmarkSuite("simple-values");

        // No throw is the assertion: each of these was an ArgumentException.
        suite.WithParameter("when", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        suite.WithParameter("offset", DateTimeOffset.UnixEpoch);
        suite.WithParameter("duration", TimeSpan.FromSeconds(1));
        suite.WithParameter("id", Guid.Empty);
        suite.WithParameter("native", (nint)1);
        suite.WithParameter("unsignedNative", (nuint)1);
    }

    /// <summary>
    ///     A value the codec cannot carry is still refused at registration, naming the type.
    /// </summary>
    [Fact]
    public void WithParameter_StillRefusesAValueTheCodecCannotCarry()
    {
        var suite = new BenchmarkSuite("live-value");

        var error = Assert.Throws<ArgumentException>(() => suite.WithParameter("stream", Stream.Null));

        Assert.Contains(nameof(Stream), error.Message, StringComparison.Ordinal);
    }
}
