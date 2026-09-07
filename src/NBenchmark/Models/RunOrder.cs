namespace NBenchmark;

/// <summary>The order in which benchmarks within a run are executed.</summary>
public enum RunOrder
{
    /// <summary>Benchmarks are shuffled into a random order.</summary>
    Random,

    /// <summary>Benchmarks run in the order they were declared/discovered.</summary>
    Declaration,
}
