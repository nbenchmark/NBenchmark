namespace NBenchmark;

/// <summary>Controls whether a benchmark class gets a fresh instance per method or per class. See <see cref="InstanceLifetime" />.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InstanceLifetimeAttribute : Attribute
{
    /// <summary>Creates the attribute with the given <paramref name="lifetime" />.</summary>
    public InstanceLifetimeAttribute(InstanceLifetime lifetime)
    {
        Lifetime = lifetime;
    }

    /// <summary>The configured instance lifetime.</summary>
    public InstanceLifetime Lifetime { get; }
}
