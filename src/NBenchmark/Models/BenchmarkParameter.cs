using System.Globalization;
using System.Text;

namespace NBenchmark;

public sealed record BenchmarkParameter(string Name, object? Value)
{
    public static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        char c => c.ToString(),
        _ => value.ToString() ?? "?",
    };

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
