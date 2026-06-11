using System.Reflection;
using NBenchmark.Attributes;

namespace NBenchmark.Discovery;

public sealed record BenchmarkSuiteDefinition(
    Type Type,
    IReadOnlyList<BenchmarkMethodDefinition> Benchmarks,
    Action<object>? SetupDelegate = null,
    Action<object>? TeardownDelegate = null
);

public sealed record BenchmarkMethodDefinition(
    MethodInfo Method,
    BenchmarkAttribute Attribute
)
{
    private readonly string? _displayName;

    /// <summary>
    ///     The name used in output and filtering. Defaults to the method name, but for
    ///     parameterised benchmarks it is <c>Method(arg1, arg2, ...)</c> so each argument
    ///     set is distinguishable.
    /// </summary>
    public string DisplayName
    {
        get => _displayName ?? Method.Name;
        init => _displayName = value;
    }

    public Func<object, object?>? SyncDelegate { get; init; }
    public Func<object, Task>? AsyncDelegate { get; init; }
    public Action<Task>? ResultConsumer { get; init; }
    public Action<object>? IterationSetupDelegate { get; init; }
    public Action<object>? IterationTeardownDelegate { get; init; }

    /// <summary>
    ///     When true, the host runs this benchmark in a dedicated child process for a
    ///     clean-room CLR, rather than in-process. Set by <c>[IsolatedProcess]</c> on the
    ///     method or its declaring class.
    /// </summary>
    public bool IsolatedProcess { get; init; }
}
