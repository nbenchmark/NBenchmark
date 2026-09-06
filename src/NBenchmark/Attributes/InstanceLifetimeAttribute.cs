namespace NBenchmark;

[AttributeUsage(AttributeTargets.Class)]
public sealed class InstanceLifetimeAttribute : Attribute
{
    public InstanceLifetimeAttribute(InstanceLifetime lifetime)
    {
        Lifetime = lifetime;
    }

    public InstanceLifetime Lifetime { get; }
}
