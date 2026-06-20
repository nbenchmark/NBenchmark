namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RuntimesAttribute : Attribute
{
    private readonly RuntimeMoniker[] _runtimes;

    public RuntimesAttribute(params RuntimeMoniker[] runtimes)
    {
        _runtimes = runtimes ?? [];
    }

    public IReadOnlyList<RuntimeMoniker> Runtimes => _runtimes;
}
