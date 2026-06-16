namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class InstanceLifetimeAttribute : Attribute
{
    public InstanceLifetime Lifetime { get; }
    public InstanceLifetimeAttribute(InstanceLifetime lifetime) => Lifetime = lifetime;
}
