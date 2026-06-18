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
}