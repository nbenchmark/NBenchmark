using System.Reflection;
using NBenchmark.Attributes;

namespace NBenchmark.Discovery;

public sealed record BenchmarkSuiteDefinition(
    Type Type,
    IReadOnlyList<BenchmarkMethodDefinition> Benchmarks,
    Action<object>? SetupDelegate = null,
    Action<object>? TeardownDelegate = null
)
{
    public InstanceLifetime Lifetime { get; init; } = InstanceLifetime.PerMethod;

    internal IReadOnlyList<RuntimeMoniker> Runtimes { get; init; } = [];
}

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

    /// <summary>
    ///     Whether this benchmark is the baseline. For parametric benchmarks all expanded
    ///     cases share the baseline flag when the method is marked
    ///     <c>[Benchmark(Baseline = true)]</c>.
    /// </summary>
    public bool IsBaseline { get; init; }

    public Func<object, object?>? SyncDelegate { get; init; }
    public Func<object, Task>? AsyncDelegate { get; init; }
    public Action<Task>? ResultConsumer { get; init; }
    public Action<object>? IterationSetupDelegate { get; init; }
    public Action<object>? IterationTeardownDelegate { get; init; }

    /// <summary>
    ///     The isolation intent declared by attributes on this benchmark or its class,
    ///     before the global <c>--in-process</c> flag is applied. Harness mode treats
    ///     <see cref="IsolationMode.Default" /> as per-class isolation.
    /// </summary>
    internal IsolationMode Isolation { get; init; }

    /// <summary>
    ///     Categories assigned to this benchmark through class-level and method-level
    ///     <see cref="BenchmarkCategoryAttribute" />, merged by union.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    ///     The parameter values for this expanded case, if part of a parameterised benchmark.
    ///     Empty when no parameters were defined.
    /// </summary>
    public IReadOnlyList<BenchmarkParameter> ParameterSet { get; init; } = [];
}

/// <summary>
///     The isolation intent a discovered benchmark declares through attributes, before
///     the harness layers on the global <c>--in-process</c> flag.
/// </summary>
internal enum IsolationMode
{
    /// <summary>No attribute - Harness mode isolates this benchmark with its class siblings.</summary>
    Default,

    /// <summary><c>[InProcess]</c> - run in the host process, never a child.</summary>
    InProcess,

    /// <summary><c>[IsolatedProcess]</c> - run alone in a dedicated child process.</summary>
    PerBenchmark,
}
