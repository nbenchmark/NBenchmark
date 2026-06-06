namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class BenchmarkArgumentsAttribute : Attribute
{
    public object[] Arguments { get; }
    public BenchmarkArgumentsAttribute(params object[] arguments)
    {
        Arguments = arguments;
    }
}
