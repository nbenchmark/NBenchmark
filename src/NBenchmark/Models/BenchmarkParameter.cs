using System.Globalization;
using System.Text;

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
        string s => s,
        char c => c.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

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

            var valueText = Convert.ToString(parameter.Value, CultureInfo.InvariantCulture)
                ?? parameter.Value.ToString()
                ?? "?";

            AppendPart(builder, valueText);
            builder.Append('@');
            AppendPart(builder, parameter.Value.GetType().FullName ?? parameter.Value.GetType().Name);
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
