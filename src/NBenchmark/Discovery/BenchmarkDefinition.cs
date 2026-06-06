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
    public Func<object, object?>? SyncDelegate { get; init; }
    public Func<object, Task>? AsyncDelegate { get; init; }
    public Func<Task, object?>? ResultExtractor { get; init; }
    public Action<object>? IterationSetupDelegate { get; init; }
    public Action<object>? IterationTeardownDelegate { get; init; }
}
