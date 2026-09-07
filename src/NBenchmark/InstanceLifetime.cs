namespace NBenchmark;

/// <summary>Controls whether a benchmark class instance is shared across its benchmark methods.</summary>
public enum InstanceLifetime
{
    /// <summary>A fresh instance is created for each benchmark method invocation.</summary>
    PerMethod = 0,

    /// <summary>One instance is shared across every benchmark method in the class.</summary>
    PerClass = 1,
}
