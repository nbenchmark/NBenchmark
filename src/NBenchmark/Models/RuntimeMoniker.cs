using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace NBenchmark;

/// <summary>
///     A .NET target framework a benchmark can be measured on, such as <c>net10.0</c>.
/// </summary>
/// <remarks>
///     <para>
///         Open rather than closed: this is a value over the target-framework moniker string, not a
///         fixed enum, because a library whose point is measuring the same code on several runtimes
///         must not need a release of its own before anyone can target a new one. The well-known
///         runtimes have static properties for discoverability; anything else parses.
///     </para>
///     <para>
///         Equality is ordinal on the moniker string, and <see cref="Parse" /> lower-cases what it
///         accepts, so <c>net10.0</c> and <c>NET10.0</c> are the same value.
///     </para>
/// </remarks>
public readonly record struct RuntimeMoniker
{
    private static readonly Regex Shape = new(
        @"^net\d+\.\d+(-[a-z0-9.]+)?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private readonly string? _targetFramework;

    /// <summary>Creates a moniker from a target-framework string, e.g. <c>"net10.0"</c>.</summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="targetFramework" /> is not a target-framework moniker.
    /// </exception>
    public RuntimeMoniker(string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        if (!TryNormalize(targetFramework, out var normalized))
        {
            throw new ArgumentException(
                $"'{targetFramework}' is not a .NET target framework moniker (expected e.g. 'net10.0').",
                nameof(targetFramework));
        }

        _targetFramework = normalized;
    }

    /// <summary>.NET 8 (<c>net8.0</c>).</summary>
    public static RuntimeMoniker Net8 => new("net8.0");

    /// <summary>.NET 9 (<c>net9.0</c>).</summary>
    public static RuntimeMoniker Net9 => new("net9.0");

    /// <summary>.NET 10 (<c>net10.0</c>).</summary>
    public static RuntimeMoniker Net10 => new("net10.0");

    /// <summary>
    ///     The target-framework moniker, e.g. <c>"net10.0"</c>. Empty for a defaulted value, which
    ///     names no runtime.
    /// </summary>
    public string TargetFramework => _targetFramework ?? "";

    /// <summary>
    ///     Parses a target framework, accepting both the moniker (<c>net10.0</c>) and the shorthand
    ///     the CLI takes (<c>net10</c>).
    /// </summary>
    /// <exception cref="FormatException"><paramref name="value" /> is not a target framework.</exception>
    public static RuntimeMoniker Parse(string value)
        => TryParse(value, out var moniker)
            ? moniker
            : throw new FormatException(
                $"'{value}' is not a .NET target framework (expected e.g. 'net10.0' or 'net10').");

    /// <summary>Parses a target framework, returning <c>false</c> rather than throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out RuntimeMoniker moniker)
    {
        if (TryNormalize(value, out var normalized))
        {
            moniker = new RuntimeMoniker(normalized);
            return true;
        }

        moniker = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => TargetFramework;

    private static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim().ToLowerInvariant();

        // "net10" is what a CLI user types; the moniker itself always carries the minor version.
        if (Regex.IsMatch(candidate, @"^net\d+$", RegexOptions.CultureInvariant))
            candidate += ".0";

        if (!Shape.IsMatch(candidate))
            return false;

        normalized = candidate;
        return true;
    }
}
