namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class BenchmarkArgumentsAttribute : Attribute
{
    public BenchmarkArgumentsAttribute(params object[] arguments)
    {
        Arguments = arguments;
    }

    public object[] Arguments { get; }
}