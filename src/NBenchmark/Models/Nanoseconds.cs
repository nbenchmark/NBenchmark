namespace NBenchmark;

/// <summary>
///     Scale factors for writing a nanosecond threshold at the magnitude it is actually meant at:
///     <c>5 * Nanoseconds.PerMillisecond</c> instead of <c>5_000_000</c>.
/// </summary>
/// <remarks>
///     <para>
///         Every duration in NBenchmark's public surface is nanoseconds, and every absolute threshold
///         is a <see cref="double" /> because it lives on an attribute, where <see cref="TimeSpan" />
///         is not a legal argument type. A millisecond-scale limit written out in nanoseconds is a
///         string of zeroes nobody can check at a glance, so these constants restore the unit without
///         adding a second property per statistic - a <c>const double</c> multiplication is still a
///         constant expression, so it is still a legal attribute argument.
///     </para>
///     <para>
///         Unit twins (<c>MaxMeanMs</c> beside <c>MaxMeanNs</c>) were the alternative and are worse:
///         each pair needs validation against its sibling, its own unset sentinel, and a failure
///         message that reports nanoseconds against a threshold the reader wrote in milliseconds.
///         There are 1,000,000 nanoseconds in a millisecond in every version of this library, so
///         inlining these into a consumer assembly can never disagree with a later one.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [PerformanceFact(MaxMedianNs = 5 * Nanoseconds.PerMillisecond)]
///     public void ParsesQuickly() => Parser.Parse(Payload);
///     </code>
/// </example>
public static class Nanoseconds
{
    /// <summary>Nanoseconds in a microsecond: <c>1,000</c>.</summary>
    public const double PerMicrosecond = 1_000;

    /// <summary>Nanoseconds in a millisecond: <c>1,000,000</c>.</summary>
    public const double PerMillisecond = 1_000_000;

    /// <summary>Nanoseconds in a second: <c>1,000,000,000</c>.</summary>
    public const double PerSecond = 1_000_000_000;
}
