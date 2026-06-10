namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class BenchmarkArgumentsAttribute(params object[] arguments) : Attribute
{
    public object[] Arguments { get; } = arguments;
}
