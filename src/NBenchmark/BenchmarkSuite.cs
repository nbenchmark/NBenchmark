using System.Diagnostics;
using System.Runtime.CompilerServices;
using NBenchmark.Diagnostics;
using NBenchmark.Engine;
using NBenchmark.Observers;
using NBenchmark.Reporters;
using NBenchmark.Stats;
using NBenchmark.Workers;

namespace NBenchmark;

/// <remarks>
///     Not sealed solely so <see cref="BenchmarkSuite{TState}" /> can extend it with typed
///     state-taking <c>Add</c> overloads. Nothing on this type is <c>public virtual</c>, and it is not
///     an extension point - a third subclass would inherit behaviour no part of the engine expects to
///     vary.
/// </remarks>
public class BenchmarkSuite(string name)
{
    private readonly List<BenchmarkEnvelope> _benchmarks = [];
    private readonly List<string> _categoryFilterExclude = [];
    private readonly List<string> _categoryFilterInclude = [];
    private readonly List<IMeasurementObserver> _observers = [];

    private readonly List<ParameterDef> _parameterDefs = [];
    private readonly List<ParameterizedAdd> _parameterizedFactories = [];

    private readonly List<IReporter> _reporters = [];
    private string? _baselineName;
    private ReportDetail _detail;

    /// <summary>
    ///     Set by <see cref="WithIsolation" /><c>(false)</c>, i.e. an explicit request to measure in
    ///     this process. Distinct from simply not having asked: the default is now to isolate when
    ///     the suite's bodies can be addressed, so "not isolated" and "asked for in-process" need to
    ///     be told apart in order to label results honestly.
    /// </summary>
    private bool _inProcessRequested;

    /// <summary>Why this suite ended up measured in the host process, when it did.</summary>
    private IsolationStatus _inProcessStatus = IsolationStatus.InProcessRequested;

    /// <summary>The session shuffle seed, so each replicate worker gets a distinct run order.</summary>
    private int? _seed;

    /// <summary>
    ///     How many launches this suite asked for. A field rather than a value on
    ///     <see cref="_options" /> because a launch is a <i>process</i>, spent by whoever coordinates
    ///     this suite's run and meaningless to a worker measuring it - see <see cref="LaunchCounts" />.
    /// </summary>
    private int _launchCount = LaunchCounts.Single;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private string[]? _pendingCategories;
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private bool _progressExplicitlySet;
    private RunOrder _runOrder = RunOrder.Random;
    private IReadOnlyList<RuntimeMoniker> _runtimes = [];
    private Action? _suiteSetup;
    private Action? _suiteTeardown;

    /// <summary>The display name of this suite.</summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Internal accessor for the configured <see cref="EnvironmentOptions" />. Exposed
    ///     so tests can verify the fluent <c>With*</c> builders without running a suite.
    /// </summary>
    internal EnvironmentOptions? Environment => _options.Environment;

    // --- Parameter-free Add overloads ---

    public BenchmarkSuite Add(string name, Action action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), (spec, ct) =>
                Task.FromResult(BenchmarkRunner.Instance.Run(name, action,
                    spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)),
            action, setup, teardown);

    public BenchmarkSuite Add(string name, Func<Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), async (spec, ct) =>
                await BenchmarkRunner.Instance.RunAsync(name, action,
                    spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false),
            action, setup, teardown);

    public BenchmarkSuite Add<T>(string name, Func<T> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), (spec, ct) =>
                Task.FromResult(BenchmarkRunner.Instance.Run(name, action,
                    spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)),
            action, setup, teardown);

    public BenchmarkSuite Add<T>(string name, Func<Task<T>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
        => AddEnvelope(name, ResolveAddCategories(categories, _pendingCategories), async (spec, ct) =>
                await BenchmarkRunner.Instance.RunAsync(name, action,
                    spec with { IterationSetup = setup, IterationTeardown = teardown }, ct).ConfigureAwait(false),
            action, setup, teardown);

    // --- Parameterized Add overloads: arity 1 ---

    public BenchmarkSuite Add<T>(string name, Action<T> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot = SlotValue<T>(values[0]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) =>
                    {
                        var val = slot();

                        return Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(val),
                            spec with { IterationSetup = setup, IterationTeardown = teardown }, ct));
                    });
            },
            [typeof(T)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<T, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot = SlotValue<T>(values[0]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) =>
                    {
                        var val = slot();

                        return await BenchmarkRunner.Instance.RunAsync(displayName,
                                async () => await action(val).ConfigureAwait(false),
                                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)
                            .ConfigureAwait(false);
                    });
            },
            [typeof(T)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T, TResult>(string name, Func<T, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot = SlotValue<T>(values[0]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) =>
                    {
                        var val = slot();

                        return Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(val),
                            spec with { IterationSetup = setup, IterationTeardown = teardown }, ct));
                    });
            },
            [typeof(T)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T, TResult>(string name, Func<T, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot = SlotValue<T>(values[0]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) =>
                    {
                        var val = slot();

                        return await BenchmarkRunner.Instance.RunAsync(displayName,
                                () => action(val),
                                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)
                            .ConfigureAwait(false);
                    });
            },
            [typeof(T)],
            action,
            setup,
            teardown));

        return this;
    }

    // --- Parameterized Add overloads: arity 2 ---

    public BenchmarkSuite Add<T1, T2>(string name, Action<T1, T2> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();

                        return Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2),
                            spec with { IterationSetup = setup, IterationTeardown = teardown }, ct));
                    });
            },
            [typeof(T1), typeof(T2)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T1, T2>(string name, Func<T1, T2, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();

                        return await BenchmarkRunner.Instance.RunAsync(displayName,
                                async () => await action(v1, v2).ConfigureAwait(false),
                                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)
                            .ConfigureAwait(false);
                    });
            },
            [typeof(T1), typeof(T2)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, TResult>(string name, Func<T1, T2, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();

                        return Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2),
                            spec with { IterationSetup = setup, IterationTeardown = teardown }, ct));
                    });
            },
            [typeof(T1), typeof(T2)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, TResult>(string name, Func<T1, T2, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();

                        return await BenchmarkRunner.Instance.RunAsync(displayName,
                                () => action(v1, v2),
                                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)
                            .ConfigureAwait(false);
                    });
            },
            [typeof(T1), typeof(T2)],
            action,
            setup,
            teardown));

        return this;
    }

    // --- Parameterized Add overloads: arity 3 ---

    public BenchmarkSuite Add<T1, T2, T3>(string name, Action<T1, T2, T3> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);
                var slot3 = SlotValue<T3>(values[2]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();
                        var v3 = slot3();

                        return Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2, v3),
                            spec with { IterationSetup = setup, IterationTeardown = teardown }, ct));
                    });
            },
            [typeof(T1), typeof(T2), typeof(T3)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, T3>(string name, Func<T1, T2, T3, Task> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);
                var slot3 = SlotValue<T3>(values[2]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();
                        var v3 = slot3();

                        return await BenchmarkRunner.Instance.RunAsync(displayName,
                                async () => await action(v1, v2, v3).ConfigureAwait(false),
                                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)
                            .ConfigureAwait(false);
                    });
            },
            [typeof(T1), typeof(T2), typeof(T3)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);
                var slot3 = SlotValue<T3>(values[2]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();
                        var v3 = slot3();

                        return Task.FromResult(BenchmarkRunner.Instance.Run(displayName, () => action(v1, v2, v3),
                            spec with { IterationSetup = setup, IterationTeardown = teardown }, ct));
                    });
            },
            [typeof(T1), typeof(T2), typeof(T3)],
            action,
            setup,
            teardown));

        return this;
    }

    public BenchmarkSuite Add<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, Task<TResult>> action,
        Action? setup = null, Action? teardown = null,
        IReadOnlyList<string>? categories = null)
    {
        var cat = ResolveAddCategories(categories, _pendingCategories);
        EnsureAddNameUnique(name);

        _parameterizedFactories.Add(new ParameterizedAdd(
            name, cat,
            (values, displayName) =>
            {
                var slot1 = SlotValue<T1>(values[0]);
                var slot2 = SlotValue<T2>(values[1]);
                var slot3 = SlotValue<T3>(values[2]);

                return new BenchmarkEnvelope(displayName, "", null, false, cat,
                    async (spec, ct) =>
                    {
                        var v1 = slot1();
                        var v2 = slot2();
                        var v3 = slot3();

                        return await BenchmarkRunner.Instance.RunAsync(displayName,
                                () => action(v1, v2, v3),
                                spec with { IterationSetup = setup, IterationTeardown = teardown }, ct)
                            .ConfigureAwait(false);
                    });
            },
            [typeof(T1), typeof(T2), typeof(T3)],
            action,
            setup,
            teardown));

        return this;
    }

    // --- Fluent parameter registration ---

    public BenchmarkSuite WithParameter<T>(string name, params T[] values)
    {
        ValidateParameterType(name, values);
        _parameterDefs.Add(new ParameterDef(name, typeof(T), values.Cast<object?>().ToArray()));
        return this;
    }

    public BenchmarkSuite WithParameter<T1, T2>(
        string name1, T1[] values1,
        string name2, T2[] values2)
    {
        ValidateParameterType(name1, values1);
        ValidateParameterType(name2, values2);
        _parameterDefs.Add(new ParameterDef(name1, typeof(T1), values1.Cast<object?>().ToArray()));
        _parameterDefs.Add(new ParameterDef(name2, typeof(T2), values2.Cast<object?>().ToArray()));
        return this;
    }

    /// <summary>
    ///     Declares a sweep whose values are <b>built</b> in the measuring process rather than sent to
    ///     it, each under the label that names it in the report.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The value overload can only carry what
    ///         <see cref="Workers.TestArgumentCodec" /> carries, and a sweep over anything else - two
    ///         payloads of different sizes, two pre-built trees, a small and a large document - was
    ///         refused for its type. That is the shape a parameter sweep is <i>for</i>, so the answer
    ///         cannot be "make the values simpler": it is to send the recipe instead of the value, which
    ///         is what every other un-sendable thing in this library already does.
    ///     </para>
    ///     <para>
    ///         Each recipe is invoked once, before that benchmark's warmup, in whichever process
    ///         measures - so preparation is never inside a reading, and on an isolated run the value is
    ///         never built here at all. Each must capture nothing, for the same reason a body must.
    ///     </para>
    ///     <para>
    ///         The label is required rather than derived. A value's own <c>ToString</c> is what names a
    ///         swept constant in the report, and a recipe has no value to ask until it runs - in the
    ///         other process. Naming it here keeps the benchmark's identity decided before anything is
    ///         measured, which is what lets a stored baseline match across runs.
    ///     </para>
    /// </remarks>
    public BenchmarkSuite WithParameter<T>(string name, params (string Label, Func<T> Recipe)[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length == 0)
            throw new ArgumentException($"Parameter '{name}' was declared with no values.", nameof(values));

        var recipes = new object?[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var (label, recipe) = values[i];

            if (recipe is null)
                throw new ArgumentException($"Parameter '{name}' has a null recipe.", nameof(values));

            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException(
                    $"Parameter '{name}' has a recipe with no label. A recipe's value does not exist "
                    + "until it runs in the measuring process, so the label is the only thing that can "
                    + "name it in the report.",
                    nameof(values));
            }

            recipes[i] = new ParameterRecipe(label, recipe);
        }

        _parameterDefs.Add(new ParameterDef(name, typeof(T), recipes));

        return this;
    }

    public BenchmarkSuite WithParameter<T1, T2, T3>(
        string name1, T1[] values1,
        string name2, T2[] values2,
        string name3, T3[] values3)
    {
        ValidateParameterType(name1, values1);
        ValidateParameterType(name2, values2);
        ValidateParameterType(name3, values3);
        _parameterDefs.Add(new ParameterDef(name1, typeof(T1), values1.Cast<object?>().ToArray()));
        _parameterDefs.Add(new ParameterDef(name2, typeof(T2), values2.Cast<object?>().ToArray()));
        _parameterDefs.Add(new ParameterDef(name3, typeof(T3), values3.Cast<object?>().ToArray()));
        return this;
    }

    // --- Private helpers ---

    /// <summary>
    ///     Switches this suite to one whose benchmarks are measured over state built by
    ///     <paramref name="prepare" />, in whichever process does the measuring.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the suite-shaped answer to the same problem
    ///         <see cref="Benchmark.Run{TState}(Func{TState}, Action{TState}, MeasurementOptions?, string, IBenchmarkProgress?, CancellationToken)" />
    ///         solves for a single body. Writing <c>var data = Build();</c> and closing over it makes
    ///         every benchmark in the suite un-addressable - and because one worker measures the set,
    ///         a single capturing body takes every sibling in-process with it. Naming the preparation
    ///         instead means the worker builds the data itself.
    ///     </para>
    ///     <para>
    ///         <paramref name="prepare" /> runs <b>once per benchmark</b>, before that benchmark's
    ///         warmup - not once for the suite. That is deliberate: two sorts sharing one array would
    ///         have the second measure what the first already sorted, and with the default random run
    ///         order which one that is would change between runs.
    ///     </para>
    ///     <para>
    ///         Call it before configuring anything else. A suite that already carries benchmarks or
    ///         settings cannot be converted without silently transplanting them, and a transplant that
    ///         forgets a field is the kind of defect that shows up as a setting that simply stopped
    ///         working - so it throws instead.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     await new BenchmarkSuite("sorting")
    ///         .WithState(() => Enumerable.Range(0, 10_000).Reverse().ToArray())
    ///         .Add("array", d => Array.Sort(d))
    ///         .Add("linq",  d => d.OrderBy(x => x).ToArray())
    ///         .WithBaseline("array")
    ///         .RunAsync();
    ///     </code>
    /// </example>
    public BenchmarkSuite<TState> WithState<TState>(Func<TState> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);

        if (DescribeConfiguration() is { } configured)
        {
            throw new InvalidOperationException(
                $"WithState must be called before the rest of the suite is configured, but {configured} "
                + "already set. Move the WithState call directly after the constructor:\n\n"
                + $"    new BenchmarkSuite(\"{Name}\").WithState(...).Add(...)\n\n"
                + "It returns a differently-typed suite so the bodies can take the prepared value, and "
                + "carrying existing configuration across would mean copying it field by field - which "
                + "fails silently the first time a field is missed.");
        }

        return new BenchmarkSuite<TState>(Name, prepare);
    }

    /// <summary>
    ///     Names what has already been configured on this suite, or <c>null</c> when it is untouched.
    ///     Used only to explain a misplaced <see cref="WithState{TState}" /> call.
    /// </summary>
    private string? DescribeConfiguration()
    {
        if (_benchmarks.Count > 0 || _parameterizedFactories.Count > 0)
            return "benchmarks have been";

        if (_parameterDefs.Count > 0)
            return "parameters have been";

        if (_reporters.Count > 0 || _observers.Count > 0)
            return "reporters or observers have been";

        if (_baselineName is not null)
            return "a baseline has been";

        if (_suiteSetup is not null || _suiteTeardown is not null)
            return "suite setup or teardown has been";

        if (_options != MeasurementOptions.Default || _launchCount != LaunchCounts.Single)
            return "measurement options have been";

        return null;
    }

    /// <summary>
    ///     Registers a benchmark measured over prepared state. Called by
    ///     <see cref="BenchmarkSuite{TState}" />, which owns the typed surface.
    /// </summary>
    internal BenchmarkSuite AddWithState<TState>(
        string name,
        Func<TState> prepare,
        Delegate body,
        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync,
        Action? setup,
        Action? teardown,
        IReadOnlyList<string>? categories)
    {
        EnsureUniqueName(name);

        _benchmarks.Add(new BenchmarkEnvelope(
            name, "", null, false, ResolveAddCategories(categories, _pendingCategories), runAsync)
        {
            Body = body,
            StateRecipes = [StateRecipe.For(prepare)],
            IterationSetup = setup,
            IterationTeardown = teardown,
        });

        return this;
    }

    /// <summary>
    ///     Records a benchmark, keeping the caller's own delegate alongside the wrapper that runs it.
    ///     <para>
    ///         The wrapper is a closure this library built, so its metadata token addresses
    ///         NBenchmark's code rather than the user's. Keeping <paramref name="body" /> is what
    ///         lets an inline suite be measured in a worker without the caller restructuring
    ///         anything - see <see cref="TryAddressBodies" />.
    ///     </para>
    /// </summary>
    private BenchmarkSuite AddEnvelope(
        string name,
        IReadOnlyList<string> categories,
        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync,
        Delegate? body = null,
        Action? iterationSetup = null,
        Action? iterationTeardown = null)
    {
        EnsureUniqueName(name);

        _benchmarks.Add(new BenchmarkEnvelope(name, "", null, false, categories, runAsync)
        {
            Body = body,

            // The delegates themselves rather than a flag saying they exist, so addressing can try to
            // carry them to the worker instead of giving up on the whole suite for having them.
            IterationSetup = iterationSetup,
            IterationTeardown = iterationTeardown,
        });

        return this;
    }

    private void EnsureUniqueName(string name)
    {
        if (_benchmarks.Any(b => b.Name == name) || _parameterizedFactories.Any(f => f.Name == name))
        {
            throw new ArgumentException(
                $"A benchmark named '{name}' has already been added to the suite. " +
                "Benchmark names must be unique - significance testing keys raw samples by name.",
                nameof(name));
        }
    }

    private void EnsureAddNameUnique(string name) => EnsureUniqueName(name);

    public BenchmarkSuite WithBaseline(string name)
    {
        _baselineName = name;
        return this;
    }

    /// <summary>
    ///     Repeats every benchmark in this suite across multiple launches - one worker process each -
    ///     and aggregates the per-launch results into cross-launch summary statistics.
    ///     Pass <see cref="LaunchCounts.Single" /> (the default) for standard single-launch measurement.
    /// </summary>
    public BenchmarkSuite WithLaunchCount(int count)
    {
        if (!LaunchCounts.IsValid(count))
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                $"LaunchCount must be between {LaunchCounts.Single} and {LaunchCounts.Max}.");
        }

        _launchCount = count;
        return this;
    }

    /// <summary>
    ///     Pins an exact measured-sample count, overriding the default confidence-interval-driven
    ///     auto-detection. Pass <c>0</c> for a dry-run.
    /// </summary>
    public BenchmarkSuite WithIterations(int iterations)
    {
        _options = _options with { Iterations = iterations };
        return this;
    }

    /// <summary>
    ///     Pins an exact warmup-sample count, overriding the default plateau-driven auto-detection.
    ///     Pass <c>0</c> to skip warmup.
    /// </summary>
    public BenchmarkSuite WithWarmup(int iterations)
    {
        _options = _options with { WarmupIterations = iterations };
        return this;
    }

    /// <summary>
    ///     Tunes the adaptive measurement loop (warmup plateau, CI-width sample count, and
    ///     ops-per-sample calibration). Use <see cref="AutoTuneOptions.Quick" /> for fast feedback
    ///     or <see cref="AutoTuneOptions.Thorough" /> for tighter intervals.
    /// </summary>
    public BenchmarkSuite WithAutoTune(AutoTuneOptions autoTune)
    {
        ArgumentNullException.ThrowIfNull(autoTune);
        _options = _options with { AutoTune = autoTune };
        return this;
    }

    /// <summary>Selects an adaptive-tuning preset (Default, Quick, or Thorough).</summary>
    public BenchmarkSuite WithAutoTune(AutoTunePreset preset)
    {
        _options = _options with { AutoTune = AutoTuneOptions.FromPreset(preset) };
        return this;
    }

    /// <summary>
    ///     Pins the number of back-to-back body invocations timed as one sample (<c>K</c>),
    ///     overriding auto-calibration. Honoured even with per-iteration setup/teardown.
    /// </summary>
    public BenchmarkSuite WithOpsPerSample(int opsPerSample)
    {
        _options = _options with { OpsPerSample = opsPerSample };
        return this;
    }

    public BenchmarkSuite WithAllocations(bool enabled = true)
    {
        _options = _options with { MeasureAllocationsOverride = enabled };
        return this;
    }

    /// <summary>Configures runtime diagnostics (GC counts, heap info, exceptions, CPU time).</summary>
    public BenchmarkSuite WithDiagnostics(DiagnosticsOptions diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _options = _options with { Diagnostics = diagnostics };
        return this;
    }

    /// <summary>Selects a diagnostics mode (None, Gc, GcAndCpu, All).</summary>
    public BenchmarkSuite WithDiagnostics(DiagnosticsMode mode)
    {
        _options = _options with { Diagnostics = DiagnosticsOptions.FromMode(mode) };
        return this;
    }

    /// <summary>
    ///     Sets the measurement profile, which bundles per-iteration GC, between-benchmark GC, and
    ///     allocation tracking. <see cref="MeasurementProfile.Realistic" /> (the default) keeps natural
    ///     GC pressure in the timing; <see cref="MeasurementProfile.Independent" /> isolates iterations
    ///     for pure-CPU measurement.
    /// </summary>
    public BenchmarkSuite WithMeasurementProfile(MeasurementProfile profile)
    {
        _options = _options with { Profile = profile };
        return this;
    }

    /// <summary>
    ///     Sets the runtime-startup configuration to measure under - JIT tiering, dynamic PGO,
    ///     ReadyToRun and GC flavour. Defaults to <see cref="RuntimeProfile.SteadyState" />.
    ///     <para>
    ///         This is the setting that requires a child process to exist: the runtime reads these
    ///         knobs once at startup, so they can only be applied to a process being launched.
    ///         Benchmarks that run in the host process report <c>"host"</c> and inherit its
    ///         configuration.
    ///     </para>
    /// </summary>
    public BenchmarkSuite WithRuntimeProfile(RuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _options = _options with { RuntimeProfile = profile };
        return this;
    }


    public BenchmarkSuite WithOutlierMode(OutlierMode mode)
    {
        _options = _options with { OutlierMode = mode };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="IOutlierDetector" /> for trimming, overriding
    ///     <see cref="WithOutlierMode" />. Pass one of the built-ins from
    ///     <see cref="OutlierDetectors" /> or your own implementation.
    /// </summary>
    /// <remarks>
    ///     Works in an isolated run when <paramref name="detector" />'s type has a parameterless
    ///     constructor, which the built-ins do - only the type name has to cross, and the worker
    ///     constructs it. A <i>configured</i> detector cannot be rebuilt that way; use
    ///     <see cref="WithOutlierDetector(Func{IOutlierDetector})" /> for those.
    /// </remarks>
    public BenchmarkSuite WithOutlierDetector(IOutlierDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _options = _options with { OutlierDetector = detector, OutlierDetectorFactory = null };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="IOutlierDetector" /> built by <paramref name="factory" />, so a
    ///     detector needing constructor arguments can still be used in an isolated run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>WithOutlierDetector(new KeepFastestDetector(0.9))</c> cannot be isolated: only a type
    ///         name crosses the boundary, and a type name cannot carry <c>0.9</c>. Rather than score the
    ///         results under a silently substituted method, the whole suite was measured in the host
    ///         process. A static factory is addressable, so the worker runs it and gets your detector
    ///         with your arguments:
    ///     </para>
    ///     <code>
    ///     .WithOutlierDetector(static () => new KeepFastestDetector(0.9))
    ///     </code>
    ///     <para>
    ///         The factory must capture nothing, for the same reason a benchmark body must. It is invoked
    ///         here as well, once, to give the coordinator the instance it scores with.
    ///     </para>
    /// </remarks>
    public BenchmarkSuite WithOutlierDetector(Func<IOutlierDetector> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _options = _options with
        {
            OutlierDetector = factory() ?? throw new ArgumentException(
                "The outlier detector factory returned null.", nameof(factory)),

            OutlierDetectorFactory = factory,
        };

        return this;
    }

    /// <summary>
    ///     Fails the run instead of measuring in this process when the suite cannot be isolated.
    /// </summary>
    /// <remarks>
    ///     The library-side equivalent of <c>--strict-isolation</c>. That flag audits results after the
    ///     run and sets an exit code, which suits a CLI in CI - it can name every offender at once. A
    ///     library caller has no exit code to read, so this throws at the point of refusal instead,
    ///     before anything is measured.
    /// </remarks>
    public BenchmarkSuite WithRequireIsolation(bool required = true)
    {
        _options = _options with { RequireIsolation = required };
        return this;
    }

    public BenchmarkSuite WithConfidenceLevel(double level)
    {
        _options = _options with { ConfidenceLevel = level };
        return this;
    }

    public BenchmarkSuite WithSignificance(bool enabled)
    {
        _options = _options with { EnableSignificance = enabled };
        return this;
    }

    public BenchmarkSuite WithSignificanceLevel(double level)
    {
        _options = _options with { SignificanceLevel = level };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="ISignificanceTest" /> strategy, overriding the engine
    ///     default (Mann-Whitney U for two groups, Kruskal-Wallis for three or more). Pass
    ///     one of the built-ins from <see cref="NBenchmark.Stats" /> or your own implementation.
    /// </summary>
    /// <remarks>
    ///     Isolatable when the type has a parameterless constructor. For a configured test, use
    ///     <see cref="WithSignificanceTest(Func{ISignificanceTest})" />.
    /// </remarks>
    public BenchmarkSuite WithSignificanceTest(ISignificanceTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        _options = _options with { SignificanceTest = test, SignificanceTestFactory = null };
        return this;
    }

    /// <summary>
    ///     Uses a custom <see cref="ISignificanceTest" /> built by <paramref name="factory" />, so a test
    ///     needing constructor arguments can still be used in an isolated run.
    /// </summary>
    /// <remarks>See <see cref="WithOutlierDetector(Func{IOutlierDetector})" /> for why this exists.</remarks>
    public BenchmarkSuite WithSignificanceTest(Func<ISignificanceTest> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _options = _options with
        {
            SignificanceTest = factory() ?? throw new ArgumentException(
                "The significance test factory returned null.", nameof(factory)),

            SignificanceTestFactory = factory,
        };

        return this;
    }

    /// <summary>
    ///     Requires a minimum strategy-defined practical effect in [0, 1] for a candidate
    ///     to be considered practically significant. Values below the threshold are reported
    ///     as NotSignificant with a <c>neg</c> magnitude label.
    /// </summary>
    public BenchmarkSuite WithMinimumPracticalEffect(double minimumDelta)
    {
        _options = _options with { MinimumPracticalEffect = minimumDelta };
        return this;
    }

    /// <summary>
    ///     Pins the benchmark process to the specified logical CPU cores for the duration
    ///     of the run, removing inter-core migration noise. Cores are zero-based and
    ///     logical (as reported by the OS). The prior affinity is restored when the run
    ///     completes. Call <see cref="WithDedicatedHostGuidance" /> alongside this to
    ///     surface a warning when the host looks unsuitable.
    /// </summary>
    public BenchmarkSuite WithHardwareAffinity(params int[] cores)
    {
        ArgumentNullException.ThrowIfNull(cores);

        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { CpuAffinity = cores },
        };

        return this;
    }

    /// <summary>
    ///     Requests the specified process priority for the duration of the run, reducing
    ///     preemption by unrelated OS work. <see cref="System.Diagnostics.ProcessPriorityClass.High" />
    ///     is the recommended value for dedicated benchmark hosts. The prior priority is
    ///     restored when the run completes. A refused elevation (common on locked-down CI
    ///     runners) is surfaced as a warning, not an error.
    /// </summary>
    public BenchmarkSuite WithProcessPriority(ProcessPriorityClass priority)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { ProcessPriority = priority },
        };

        return this;
    }

    /// <summary>
    ///     Enables a non-fatal pre-run probe that warns when the host looks like a
    ///     shared or otherwise noisy benchmark environment: a low CPU core count,
    ///     an unraisable process priority, or (on macOS) unobservable frequency scaling
    ///     and thermal throttling. The run still proceeds - this is guidance, not a gate.
    /// </summary>
    public BenchmarkSuite WithDedicatedHostGuidance(bool enabled = true)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with { DedicatedHostGuidance = enabled },
        };

        return this;
    }

    /// <summary>
    ///     Suppresses the always-on Debug-build / debugger-attached guidance warning for
    ///     this suite. Use when measuring Debug behavior is intentional.
    /// </summary>
    public BenchmarkSuite WithSuppressBuildConfigurationWarning(bool suppress = true)
    {
        _options = _options with
        {
            Environment = (_options.Environment ?? new EnvironmentOptions()) with
            {
                SuppressBuildConfigurationWarning = suppress,
            },
        };

        return this;
    }

    /// <summary>
    ///     Pins the shuffle seed, so a randomized run order is reproducible.
    ///     <para>
    ///         Each replicate derives a distinct order from this one seed, so raising
    ///         <see cref="WithLaunchCount" /> still varies the order between replicates - turning run
    ///         order into a randomized nuisance factor rather than a fixed confound - while the whole
    ///         session remains reproducible from a single number.
    ///     </para>
    /// </summary>
    public BenchmarkSuite WithSeed(int seed)
    {
        _seed = seed;
        return this;
    }

    public BenchmarkSuite WithRunOrder(RunOrder order)
    {
        _runOrder = order;
        return this;
    }

    public BenchmarkSuite WithSuiteSetup(Action setup)
    {
        _suiteSetup = setup;
        return this;
    }

    public BenchmarkSuite WithSuiteTeardown(Action teardown)
    {
        _suiteTeardown = teardown;
        return this;
    }

    public BenchmarkSuite WithReporter(IReporter reporter)
    {
        reporter.Detail = _detail;
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkSuite WithDetail(ReportDetail detail)
    {
        _detail = detail;

        foreach (var reporter in _reporters)
        {
            reporter.Detail = detail;
        }

        return this;
    }

    public BenchmarkSuite WithProgress(IBenchmarkProgress progress)
    {
        _progress = progress;
        _progressExplicitlySet = true;
        return this;
    }

    /// <summary>
    ///     Attaches a non-perturbing measurement observer that receives live per-sample,
    ///     per-detector, and phase-transition events during the adaptive measurement loop.
    ///     The observer MUST return immediately from each callback - never block, allocate on
    ///     the hot path, or do I/O - because the loop calls it between samples on the
    ///     measurement thread. The default is <see cref="NullMeasurementObserver" />.
    ///     Repeatable: each call adds another observer, and all attached observers receive
    ///     every event through a <see cref="CompositeMeasurementObserver" /> fan-out.
    /// </summary>
    public BenchmarkSuite WithObserver(IMeasurementObserver observer)
    {
        if (observer is not null && observer != NullMeasurementObserver.Instance)
            _observers.Add(observer);

        return this;
    }

    /// <summary>
    ///     Resolves the attached observer list to the single <see cref="IMeasurementObserver" />
    ///     the engine should see. Composes three sources: programmatic instances added via
    ///     <see cref="WithObserver(IMeasurementObserver)" />, CLI-supplied names resolved
    ///     through <see cref="ObserverRegistry" /> (in harness-hosted children), and
    ///     auto-attached observers registered via
    ///     <see cref="ObserverRegistry.RegisterAutoAttach" />. Dedup is by name across all
    ///     three sources so <c>.WithObserver(new StudioLiveObserver())</c> and the auto-attached
    ///     <c>studio</c> registration produce one <c>studio</c> stream, not two. An empty
    ///     result collapses to <see cref="NullMeasurementObserver.Instance" /> so the hot-path
    ///     guard stays false and the loop pays no dispatch cost.
    /// </summary>
    private IMeasurementObserver ResolveObserver()
    {
        // Dedup by Name (last wins) so two programmatic .WithObserver(...) calls for the
        // same named observer fire the later (more deliberate) instance, not both.
        // Anonymous observers (Name = null) are always kept. Mirrors the harness-side
        // ResolveObserver dedup.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programmatic = new List<IMeasurementObserver>(_observers.Count);

        for (var i = _observers.Count - 1; i >= 0; i--)
        {
            var observer = _observers[i];

            if (observer == NullMeasurementObserver.Instance)
                continue;

            if (!string.IsNullOrEmpty(observer.Name))
            {
                if (!seenNames.Add(observer.Name))
                    continue;
            }

            programmatic.Add(observer);
        }

        programmatic.Reverse();

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var observer in programmatic)
        {
            if (!string.IsNullOrEmpty(observer.Name))
                explicitNames.Add(observer.Name);
        }

        var autoAttached = ObserverRegistry.CreateAutoAttachedObservers(explicitNames);

        if (programmatic.Count == 0 && autoAttached.Count == 0)
            return NullMeasurementObserver.Instance;

        if (programmatic.Count == 1 && autoAttached.Count == 0)
            return programmatic[0];

        var all = new List<IMeasurementObserver>(programmatic.Count + autoAttached.Count);
        all.AddRange(programmatic);
        all.AddRange(autoAttached);

        return all.Count switch
        {
            0 => NullMeasurementObserver.Instance,
            1 => all[0],
            _ => new CompositeMeasurementObserver(all),
        };
    }

    /// <summary>
    ///     Resolves observer names forwarded by a coordinator
    ///     into a single <see cref="IMeasurementObserver" /> for an isolated suite child. The child
    ///     re-runs the entry assembly, so <c>[ModuleInitializer]</c> self-registration populates
    ///     <see cref="ObserverRegistry" /> identically and the names resolve to the same factories.
    ///     Auto-attached observers also fire in children (dedup'd against the forwarded explicit
    ///     names so <c>--observer studio</c> does not double-attach). An empty list collapses to
    ///     <see cref="NullMeasurementObserver.Instance" />.
    /// </summary>
    private static IMeasurementObserver ResolveChildObservers(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            // No explicit names forwarded, but auto-attached observers still fire in the
            // child (e.g. a live-streaming observer referenced by the parent's entry
            // assembly). Resolve them with an empty dedup set.
            var autoAttachedOnly = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            return autoAttachedOnly.Count switch
            {
                0 => NullMeasurementObserver.Instance,
                1 => autoAttachedOnly[0],
                _ => new CompositeMeasurementObserver(autoAttachedOnly),
            };
        }

        var resolved = new List<IMeasurementObserver>(names.Count);

        foreach (var name in names)
        {
            if (ObserverRegistry.TryCreate(name, out var observer)
                && observer != NullMeasurementObserver.Instance)
                resolved.Add(observer);
        }

        // Auto-attached observers also fire in children. EnsureExtensionsLoaded (called by
        // CreateAutoAttachedObservers) has loaded NBenchmark.* assemblies (including
        // NBenchmark.Studio, if referenced) and their [ModuleInitializer]s have registered
        // auto-attached observers. Dedup against the request's explicit observer names.
        var explicitNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var autoAttached = ObserverRegistry.CreateAutoAttachedObservers(explicitNames);
        resolved.AddRange(autoAttached);

        return resolved.Count switch
        {
            0 => NullMeasurementObserver.Instance,
            1 => resolved[0],
            _ => new CompositeMeasurementObserver(resolved),
        };
    }

    /// <summary>
    ///     Tags every subsequent benchmark added to the suite with the supplied categories.
    ///     <c>.WithCategories()</c> does not affect benchmarks already added.
    /// </summary>
    public BenchmarkSuite WithCategories(params string[] categories)
    {
        _pendingCategories = NormalizeCategories(categories, nameof(categories));
        return this;
    }

    /// <summary>
    ///     Filters the suite by category before running. Include rules are OR: a benchmark
    ///     runs if it has any included category. Exclude rules are also OR: a benchmark is
    ///     removed if it has any excluded category. Untagged benchmarks are excluded when
    ///     any include filter is set.
    /// </summary>
    public BenchmarkSuite WithCategoryFilter(IEnumerable<string>? include = null, IEnumerable<string>? exclude = null)
    {
        if (include is not null)
            AddCategories(_categoryFilterInclude, include, nameof(include));

        if (exclude is not null)
            AddCategories(_categoryFilterExclude, exclude, nameof(exclude));

        return this;
    }

    /// <summary>
    ///     Whether to measure this suite in a worker process. Isolation is the default, so the only
    ///     call that changes anything is <c>WithIsolation(false)</c> - an explicit request to measure
    ///     in the current process.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Reach for <c>false</c> when the current process <i>is</i> the subject: cold-start cost, or
    ///         a body that must observe host state a fresh process cannot rebuild. The result is stamped
    ///         <see cref="IsolationStatus.InProcessRequested" /> and reports the host's runtime
    ///         configuration, so it is never silently compared against an isolated measurement.
    ///     </para>
    ///     <para>
    ///         <c>WithIsolation()</c> with no argument is therefore a no-op kept for source
    ///         compatibility - it asks for what already happens. Only the request for the host process
    ///         is recorded, because that is the only one that changes anything.
    ///     </para>
    ///     <para>
    ///         That makes it subtly unlike <see cref="BenchmarkHarness.WithIsolation" />, which shares
    ///         this name and signature but is a global switch settable in both directions - a harness
    ///         measures many classes, so re-enabling isolation after disabling it means something there.
    ///         A suite is one group, so there is nothing to re-enable; <c>true</c> only ever restates
    ///         the default.
    ///     </para>
    /// </remarks>
    public BenchmarkSuite WithIsolation(bool enabled = true)
    {
        // Only the in-process request is stored. There was a second field tracking `enabled` itself,
        // which nothing ever read once isolation became the default - dead state whose presence
        // implied a decision was being made here that was not.
        //
        // WithIsolation(false) is a real instruction rather than the default, because a suite whose
        // bodies can be addressed is measured in a worker without being asked. Recording the request
        // separately is what lets the report distinguish "you chose the host process" from "your suite
        // could not be isolated", which have entirely different remedies.
        _inProcessRequested = !enabled;

        return this;
    }

    /// <summary>
    ///     Runs the suite benchmarks under each specified runtime and compares results.
    ///     Cross-runtime execution is always isolated (each runtime is built via
    ///     <c>dotnet build -f &lt;tfm&gt;</c> and measured in that build's own worker),
    ///     regardless of the <see cref="WithIsolation" /> setting - measuring another framework's
    ///     build in this process is not something the host can do.
    /// </summary>
    public BenchmarkSuite WithRuntimes(params RuntimeMoniker[] runtimes)
    {
        _runtimes = runtimes;
        return this;
    }

    /// <summary>
    ///     Builds a suite with <paramref name="plan" /> and measures it in a dedicated worker
    ///     process, then scores and reports it here.
    /// </summary>
    /// <param name="plan">
    ///     A method group pointing at a <b>static, non-capturing</b> factory that builds and
    ///     configures the suite - conventionally marked <c>[BenchmarkPlan]</c>. The method group
    ///     itself is the address: the worker locates that method by metadata token and invokes it,
    ///     so the suite is constructed in the process that measures it.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         This is the isolated entry point for Suite mode, and it is strictly better than
    ///         <see cref="WithIsolation" /> was. That mechanism re-executed the whole program to
    ///         rebuild the suite, so <i>M</i> isolated suites in one <c>Main</c> did <i>M²</i>
    ///         measurement work and every side effect in <c>Main</c> re-ran once per child. Here the
    ///         worker calls one factory and nothing else.
    ///     </para>
    ///     <para>
    ///         Because the worker runs your factory rather than deserializing a description of it,
    ///         everything the suite holds is live in the measuring process: benchmark bodies, suite
    ///         setup and teardown, a custom <see cref="Stats.IOutlierDetector" /> or
    ///         <see cref="Stats.ISignificanceTest" />, an instance factory. None of it has to be
    ///         serializable.
    ///     </para>
    ///     <para>
    ///         A factory that captures state from its enclosing scope cannot be addressed, and is
    ///         refused rather than approximated: the suite is then measured in this process, the
    ///         reason is printed, and every result is stamped accordingly. Make the factory
    ///         <c>static</c> to isolate it.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     await BenchmarkSuite.RunPlanAsync(BuildSuite);
    ///
    ///     [BenchmarkPlan]
    ///     static BenchmarkSuite BuildSuite() =>
    ///         new BenchmarkSuite("comparison")
    ///             .Add("baseline", () => Baseline())
    ///             .Add("candidate", () => Candidate())
    ///             .WithBaseline("baseline")
    ///             .WithReporter(new ConsoleReporter());
    ///     </code>
    /// </example>
    public static async Task<IReadOnlyList<BenchmarkResult>> RunPlanAsync(
        Func<BenchmarkSuite> plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Built here as well as in the worker. The coordinator needs the reporters, the baseline and
        // the runtime profile to launch under - and building it is cheap, because a factory only
        // wires delegates up rather than running them.
        var local = plan() ?? throw new InvalidOperationException(
            $"The benchmark plan '{plan.Method.Name}' returned null.");

        local.ValidateBaseline();

        if (!local._progressExplicitlySet)
            local._progress = new DefaultConsoleProgress();

        using var observer = local.ResolveObserver();

        if (local._runtimes.Count > 0)
        {
            return await SuitePlanRunner
                .RunAcrossRuntimesAsync(plan, local, local._progress, observer, cancellationToken)
                .ConfigureAwait(false);
        }

        var outcome = await SuitePlanRunner
            .RunAsync(plan, local, local._progress, observer, sessionSeed: null, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.WasIsolated)
        {
            // The *plan* could not be addressed. That is not the same as the suite being
            // unmeasurable in a worker: RunCoreAsync tries the inline path next, where the bodies are
            // addressed individually, and that frequently succeeds where the factory did not - a
            // factory capturing a local is refused while the non-capturing bodies it wires up are
            // not. So this says what was refused and stops there, rather than announcing an outcome
            // that has not happened yet.
            SimpleModeGuidance.EmitPlanRefusal(local.Name, outcome.Status, outcome.Refusal);

            // Whatever RunCoreAsync does - worker or host - it stamps its own results, so the label
            // describes where the measurement actually happened. Overwriting them with the plan's
            // refusal status was wrong in the case that matters: a suite the inline path isolated
            // came back marked host-measured, the reporters (invoked inside the isolated path) said
            // Isolated while the returned list said otherwise, and --strict-isolation failed a run
            // that had been fully isolated.
            return await local.RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        var results = outcome.Results.ToList();
        var rawSamples = outcome.RawSamples.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        await local._progress.OnSuiteCompleted(results).ConfigureAwait(false);

        observer.OnPhase(new MeasurementPhaseEvent(
            string.Empty, MeasurementPhase.SuiteCompleted, PhaseTransition.Completed, Succeeded: true));

        local.ApplyPerParameterSignificance(results, rawSamples);
        await local.InvokeReportersAsync(results, cancellationToken).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    ///     Finds every <c>[BenchmarkPlan]</c> factory on <typeparamref name="T" /> and runs each one
    ///     in its own measurement worker, in declaration order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each plan gets a fresh worker, so several suites in one program cost one worker each -
    ///         linear, not the <i>M²</i> the previous callsite-replay design produced by re-running
    ///         the whole program per child.
    ///     </para>
    ///     <para>
    ///         Plans are conventionally grouped on a <c>static class</c>, which C# does not allow as
    ///         a type argument - use <see cref="RunPlansAsync(Type, CancellationToken)" /> for those.
    ///     </para>
    /// </remarks>
    public static Task<IReadOnlyList<BenchmarkResult>> RunPlansAsync<T>(
        CancellationToken cancellationToken = default)
        => RunPlansAsync(typeof(T), cancellationToken);

    /// <inheritdoc cref="RunPlansAsync{T}(CancellationToken)" />
    public static async Task<IReadOnlyList<BenchmarkResult>> RunPlansAsync(
        Type declaringType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaringType);

        var plans = BenchmarkPlanDiscovery.Find(declaringType);

        if (plans.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{declaringType.Name}' declares no benchmark plans. A plan is a static, "
                + $"parameterless method returning {nameof(BenchmarkSuite)} and marked "
                + "[BenchmarkPlan].");
        }

        var all = new List<BenchmarkResult>();

        foreach (var plan in plans)
        {
            all.AddRange(await RunPlanAsync(plan, cancellationToken).ConfigureAwait(false));
        }

        return all;
    }

    /// <summary>
    ///     Runs every benchmark in the suite and returns their results.
    ///     <para>
    ///         Measured in a worker process whenever every body in the suite can be addressed there,
    ///         which needs no attribute and no change to how the suite was written. When something
    ///         cannot cross the boundary - a captured local, a setup delegate, a parameter sweep - the
    ///         suite is measured here instead, the reason is printed once, and every result carries
    ///         the matching <see cref="IsolationStatus" />.
    ///     </para>
    ///     <para>
    ///         <see cref="RunPlanAsync(Func{BenchmarkSuite}, CancellationToken)" /> is the answer for
    ///         the suites this cannot handle: the worker invokes your factory, so the bodies, the
    ///         lifecycle delegates and any custom strategy are live objects it built rather than
    ///         anything that had to be serialized.
    ///     </para>
    /// </summary>
    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
        => RunCoreAsync(cancellationToken);

    private async Task<IReadOnlyList<BenchmarkResult>> RunCoreAsync(CancellationToken cancellationToken)
    {
        ValidateBaseline();

        if (_runtimes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Suite '{Name}' asks for multiple runtimes, which needs a static [BenchmarkPlan] "
                + "factory: run it with BenchmarkSuite.RunPlanAsync(BuildSuite).\n\n"
                + "Measuring another target framework means measuring a different build of your "
                + "code, and an inline suite's benchmark bodies are located by metadata token - a "
                + "number that only means anything inside the build that produced it. A factory is "
                + "found by name instead, which is stable across builds, so the worker for each "
                + "runtime can construct the suite from that runtime's own assemblies.");
        }

        if (!_progressExplicitlySet)
            _progress = new DefaultConsoleProgress();

        using var observer = ResolveObserver();

        // An ordinary inline suite is measured in a worker when its bodies can be addressed - which
        // needs no factory, no attribute and no change to how the suite was written. Isolation that
        // costs ergonomics is isolation people turn off, so the accurate path has to be the
        // effortless one.
        if (!_inProcessRequested)
        {
            var isolated = await TryRunInWorkerAsync(observer, cancellationToken).ConfigureAwait(false);

            if (isolated is not null)
                return isolated;
        }

        return await RunInProcessCoreAsync(
            _progress,
            observer,
            _runOrder,
            true,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Measures this inline suite in a worker, or returns <c>null</c> when it cannot be - having
    ///     first said why, and what to do about it.
    /// </summary>
    private async Task<IReadOnlyList<BenchmarkResult>?> TryRunInWorkerAsync(
        IMeasurementObserver observer,
        CancellationToken cancellationToken)
    {
        var expanded = ExpandEnvelopes();
        var benchmarks = ApplyCategoryFilter(ApplyExecutionOrder(expanded, RunOrder.Declaration));

        var decision = InlineSuitePlan.TryAddress(benchmarks, _options, _suiteSetup, _suiteTeardown);

        if (!decision.CanIsolate)
        {
            IsolationAudit.ThrowIfRequired(_options, Name, decision.Status, decision.Explanation);

            SimpleModeGuidance.EmitOnce(Name, decision.Status, decision.Explanation);
            _inProcessStatus = decision.Status;

            return null;
        }

        var replicates = _launchCount;
        var timeout = MeasurementBudget.For(_options, decision.Bodies.Count);

        var perReplicate = new List<IReadOnlyList<BenchmarkResult>>(replicates);
        var perReplicateSamples = new List<Dictionary<string, double[]>>(replicates);
        var faults = new List<FaultPayload>();

        for (var replicate = 0; replicate < replicates; replicate++)
        {
            var request = InlineSuitePlan.Request(
                Name, decision.Bodies, _options, _runOrder,
                WorkerRunPlan.DeriveSeed(_seed, replicate), replicate,
                decision.SuiteSetup, decision.SuiteTeardown, decision.Receivers);

            var group = await WorkerLauncher.Current.RunGroupAsync(
                    request,
                    replicate == 0 ? _progress : NullBenchmarkProgress.Instance,
                    replicate == 0 ? observer : NullMeasurementObserver.Instance,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            faults.AddRange(group.Faults);

            if (group.Results.Count > 0)
            {
                perReplicate.Add(group.Results);
                perReplicateSamples.Add(group.RawSamples);
            }
        }

        if (perReplicate.Count == 0)
        {
            // Nothing came back. Measuring here is better than returning nothing, and the caller is
            // told it is not getting what it asked for.
            SimpleModeGuidance.EmitOnce(
                Name,
                IsolationStatus.InProcessNoWorker,
                faults.FirstOrDefault()?.Message ?? "no measurement worker produced a result.");

            _inProcessStatus = IsolationStatus.InProcessNoWorker;

            return null;
        }

        var (results, rawSamples) = SuitePlanRunner.Combine(perReplicate, perReplicateSamples);

        // The suite's own baseline choice is applied here, because the worker measured bare bodies
        // and has no idea which of them the comparison is against.
        for (var i = 0; i < results.Count; i++)
        {
            results[i] = results[i] with
            {
                ClassName = "",
                IsBaseline = _baselineName is not null && results[i].Name == _baselineName,
            };
        }

        await _progress.OnSuiteCompleted(results).ConfigureAwait(false);

        observer.OnPhase(new MeasurementPhaseEvent(
            string.Empty, MeasurementPhase.SuiteCompleted, PhaseTransition.Completed, Succeeded: true));

        ApplyPerParameterSignificance(results, rawSamples);
        await InvokeReportersAsync(results, cancellationToken).ConfigureAwait(false);

        return results;
    }

    private async Task<IReadOnlyList<BenchmarkResult>> RunInProcessCoreAsync(
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        RunOrder order,
        bool applySignificance,
        bool applyReporters,
        CancellationToken cancellationToken)
    {
        var expanded = ExpandEnvelopes();
        var ordered = ApplyExecutionOrder(expanded, order);
        var filteredBenchmarks = ApplyCategoryFilter(ordered);
        var envelopeNames = filteredBenchmarks.Select(b => b.Name).ToList();

        NBenchmarkDiagnostics.OnSuiteStarting(Name, filteredBenchmarks.Count, _options.Profile.ToString(),
            _runtimes.Count > 0 ? string.Join(",", _runtimes.Select(r => r.ToTargetFramework())) : null, runOrder: _runOrder.ToString());

        List<BenchmarkResult> results = [];
        var sentinelEmitted = false;

        try
        {
            await progress.OnSuiteStarting(envelopeNames, filteredBenchmarks.Count).ConfigureAwait(false);

            // Apply opt-in hardware/OS controls for the duration of the in-process run. The scope
            // restores the prior process state on dispose. A worker measuring this suite applies the
            // same settings to itself - see MeasureInWorkerAsync.
            using var _ = EnvironmentControl.Apply(_options.Environment);

            _suiteSetup?.Invoke();

            var envelopes = filteredBenchmarks
                .Select(b => b with { IsBaseline = _baselineName is not null && b.OriginalName == _baselineName })
                .ToList();

            Dictionary<string, double[]> rawSamples;

            try
            {
                var effectiveOrder = _parameterDefs.Count > 0 ? RunOrder.Declaration : order;

                if (_launchCount > 1)
                {
                    var allLaunchResults = new List<IReadOnlyList<BenchmarkResult>>();
                    var allLaunchSamples = new List<Dictionary<string, double[]>>();

                    for (var launchIdx = 0; launchIdx < _launchCount; launchIdx++)
                    {
                        var launchObserver = launchIdx == 0 ? observer : NullMeasurementObserver.Instance;

                        var (launchResults, launchSamples) = await SuiteRunner.RunAsync(
                            envelopes, effectiveOrder, null, _options, 0,
                            filteredBenchmarks.Count, NullBenchmarkProgress.Instance, cancellationToken,
                            null, launchObserver).ConfigureAwait(false);

                        allLaunchResults.Add(launchResults);
                        allLaunchSamples.Add(launchSamples);
                    }

                    (results, rawSamples) = AggregateSuiteLaunches(allLaunchResults, allLaunchSamples);
                }
                else
                {
                    (results, rawSamples) = await SuiteRunner.RunAsync(
                        envelopes, effectiveOrder, null, _options, 0,
                        filteredBenchmarks.Count, progress, cancellationToken,
                        null, observer).ConfigureAwait(false);
                }
            }
            finally
            {
                _suiteTeardown?.Invoke();
            }

            await progress.OnSuiteCompleted(results).ConfigureAwait(false);

            // SuiteCompleted sentinel: emit on the success path with Succeeded = true. A
            // live-streaming observer treats this as the authoritative run-end signal.
            observer.OnPhase(new MeasurementPhaseEvent(
                string.Empty,
                MeasurementPhase.SuiteCompleted,
                PhaseTransition.Completed,
                Succeeded: true));

            sentinelEmitted = true;

            // SuiteRunner keys raw samples by benchmark name; the significance path needs the
            // composite name+runtime key so multi-runtime results don't collide.
            rawSamples = RawSampleKey.ToComposite(results, rawSamples);

            // Why these rows were measured here, on the rows themselves. Written last so it covers
            // every path into this method, and before the reporters so the file and the returned list
            // agree. Without it the fallback kept BenchmarkResult's default - InProcessRequested,
            // "you asked for this" - so a suite refused for a captured local reported the status of
            // one that had asked for the host, produced no remedy footer and no Iso column. The
            // status was computed and assigned to a field nothing read.
            for (var i = 0; i < results.Count; i++)
            {
                // An errored row was not measured anywhere, so it has no provenance to carry.
                if (!results[i].Errored)
                    results[i] = results[i] with { IsolationStatus = _inProcessStatus };
            }

            if (applySignificance)
                ApplyPerParameterSignificance(results, rawSamples);

            if (applyReporters)
                await InvokeReportersAsync(results, cancellationToken).ConfigureAwait(false);

            return results;
        }
        finally
        {
            // If the try block did not reach its success-path emit (a suite-level
            // exception prevented it), emit the sentinel here with Succeeded = false so a
            // live-streaming observer can finalise the run as Failed rather than leaving it
            // stuck as Running until the idle timeout fires.
            if (!sentinelEmitted)
            {
                observer.OnPhase(new MeasurementPhaseEvent(
                    string.Empty,
                    MeasurementPhase.SuiteCompleted,
                    PhaseTransition.Completed,
                    Succeeded: false));
            }

            NBenchmarkDiagnostics.OnSuiteCompleted(results);
        }
    }

    /// <summary>
    ///     Measures this suite inside a measurement worker and hands the results back over the pipe.
    ///     <para>
    ///         This is the whole payoff of the <c>[BenchmarkPlan]</c> approach: the worker built this
    ///         suite by invoking the user's own factory, so the bodies, the setup and teardown
    ///         delegates, any custom <see cref="Stats.IOutlierDetector" /> or
    ///         <see cref="Stats.ISignificanceTest" />, and any instance factory are all <i>live
    ///         objects in the worker's own process</i>. Nothing was serialized, so nothing can be
    ///         lost in translation - which is what the previous design's "cannot cross the boundary"
    ///         list was entirely made of.
    ///     </para>
    ///     <para>
    ///         Reporters and significance stay with the coordinator, which owns presentation and can
    ///         see all replicates. So does the launch count, which this method does not read at all:
    ///         the coordinator spends it on replicate workers, and honouring the factory's
    ///         <c>WithLaunchCount</c> again inside one of them would multiply the two.
    ///     </para>
    /// </summary>
    internal async Task<(List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples)>
        MeasureInWorkerAsync(
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            int? seed,
            int startIndex,
            int totalBenchmarks,
            CancellationToken cancellationToken)
    {
        var expanded = ExpandEnvelopes();
        var ordered = ApplyExecutionOrder(expanded, _runOrder);
        var filtered = ApplyCategoryFilter(ordered);

        // The runtime profile was applied to this process's environment block before it started -
        // the only moment it could have been. Affinity and priority are settable at any time and
        // belong here.
        using var _ = EnvironmentControl.Apply(_options.Environment);

        _suiteSetup?.Invoke();

        try
        {
            var envelopes = filtered
                .Select(b => b with { IsBaseline = _baselineName is not null && b.OriginalName == _baselineName })
                .ToList();

            // Parameterized suites pin declaration order so a parameter sweep reads in the order it
            // was declared; everything else honours the suite's configured order, shuffled by the
            // seed this replicate was given.
            var effectiveOrder = _parameterDefs.Count > 0 ? RunOrder.Declaration : _runOrder;

            var (results, rawSamples) = await SuiteRunner.RunAsync(
                    envelopes,
                    effectiveOrder,
                    seed,
                    _options,
                    startIndex,
                    totalBenchmarks,
                    progress,
                    cancellationToken,
                    null,
                    observer)
                .ConfigureAwait(false);

            return (results.ToList(), rawSamples);
        }
        finally
        {
            _suiteTeardown?.Invoke();
        }
    }

    /// <summary>The benchmark names this suite would measure, for the coordinator's progress and error rows.</summary>
    internal IReadOnlyList<string> BenchmarkNames()
        => ApplyCategoryFilter(ApplyExecutionOrder(ExpandEnvelopes(), RunOrder.Declaration))
            .Select(b => b.Name)
            .ToList();

    /// <summary>The measurement configuration this suite was built with.</summary>
    internal MeasurementOptions ResolvedOptions => _options;

    /// <summary>
    ///     How many launches this suite asked for, read by whoever coordinates its run.
    /// </summary>
    internal int ResolvedLaunchCount => _launchCount;

    /// <summary>The target frameworks this suite asked to be measured against.</summary>
    internal IReadOnlyList<RuntimeMoniker> RequestedRuntimes => _runtimes;

    /// <summary>Applies significance to results measured elsewhere, using this suite's configuration.</summary>
    internal void ScoreAndReport(List<BenchmarkResult> results, Dictionary<string, double[]> rawSamples)
        => ApplyPerParameterSignificance(results, rawSamples);

    /// <summary>Runs this suite's reporters over results measured elsewhere.</summary>
    internal Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
        => InvokeReportersAsync(results, cancellationToken);

    private static (List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples) AggregateSuiteLaunches(
        IReadOnlyList<IReadOnlyList<BenchmarkResult>> allLaunchResults,
        IReadOnlyList<Dictionary<string, double[]>> allLaunchSamples)
    {
        if (allLaunchResults.Count == 0)
            return ([], []);

        var names = allLaunchResults[0].Select(r => r.Name).ToList();
        var aggregated = new List<BenchmarkResult>(names.Count);
        var rawSamples = new Dictionary<string, double[]>(names.Count);
        var pooledSamples = PoolRawSamplesByName(allLaunchSamples);

        foreach (var name in names)
        {
            // Zipped before filtering, so a launch that produced no result for this benchmark drops
            // its samples with it rather than shifting later launches' samples onto the wrong result.
            var launches = allLaunchResults
                .Select((launch, i) => (Result: launch.FirstOrDefault(r => r.Name == name), Index: i))
                .Where(x => x.Result is not null)
                .Select(x => new LaunchAggregator.Launch(
                    x.Result!, allLaunchSamples[x.Index].GetValueOrDefault(name, [])))
                .ToList();

            if (launches.Count == 0)
                continue;

            var combined = LaunchAggregator.Combine(launches);
            aggregated.Add(combined);

            if (pooledSamples.TryGetValue(name, out var samples))
                rawSamples[RawSampleKey.For(name, combined.RuntimeMoniker)] = samples;
        }

        return (aggregated, rawSamples);
    }

    private static Dictionary<string, double[]> PoolRawSamplesByName(
        IReadOnlyList<Dictionary<string, double[]>> perLaunchSamples)
    {
        var pooled = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (var launch in perLaunchSamples)
        {
            foreach (var (name, samples) in launch)
            {
                if (!pooled.TryGetValue(name, out var bucket))
                {
                    bucket = [];
                    pooled[name] = bucket;
                }

                bucket.AddRange(samples);
            }
        }

        return pooled.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.Ordinal);
    }

    private List<BenchmarkEnvelope> ExpandEnvelopes()
    {
        if (_parameterDefs.Count == 0)
        {
            if (_parameterizedFactories.Count > 0)
            {
                throw new InvalidOperationException(
                    "Parameterized benchmarks were registered but no WithParameter call was made. " +
                    "Add parameter values with WithParameter before running the suite, " +
                    "or register benchmarks without typed lambda parameters.");
            }

            return [.. _benchmarks];
        }

        if (_parameterizedFactories.Count == 0)
        {
            throw new InvalidOperationException(
                "WithParameter was called but no parameterized benchmarks (Add with typed lambda) were registered.");
        }

        var combinations = ComputeParameterCombinations();
        var usedNames = new HashSet<string>(_benchmarks.Select(b => b.Name));
        var expanded = new List<BenchmarkEnvelope>();

        var parameterTypes = _parameterDefs.Select(d => d.Type).ToArray();
        var compatibleFactories = new List<ParameterizedAdd>();

        foreach (var factory in _parameterizedFactories)
        {
            if (factory.ParamTypes.Length != _parameterDefs.Count)
                continue;

            if (!AreTypesCompatible(factory.ParamTypes, parameterTypes))
            {
                throw new InvalidOperationException(
                    $"Benchmark '{factory.Name}' parameter types ({string.Join(", ", factory.ParamTypes.Select(t => t.Name))}) " +
                    $"do not match the registered WithParameter types ({string.Join(", ", _parameterDefs.Select(d => d.Type.Name))}).");
            }

            compatibleFactories.Add(factory);
        }

        foreach (var combo in combinations)
        {
            var paramSet = new BenchmarkParameter[combo.Length];

            for (var i = 0; i < combo.Length; i++)
            {
                // A recipe's label stands in for the value, which does not exist yet and may never
                // exist in this process at all.
                paramSet[i] = new BenchmarkParameter(
                    _parameterDefs[i].Name,
                    combo[i] is ParameterRecipe recipe ? recipe.Label : combo[i]);
            }

            foreach (var factory in compatibleFactories)
            {
                var displayName = FormatParamDisplayName(factory.Name, paramSet);

                if (!usedNames.Add(displayName))
                {
                    throw new ArgumentException(
                        $"Duplicate benchmark name after parameter expansion: '{displayName}'. " +
                        "Ensure parameter values produce unique display names.");
                }

                var slots = combo.ToArray();
                var envelope = factory.Factory(slots, displayName);

                // Split for the wire: a swept constant is sent as a value, a recipe is sent as the
                // address of the factory that builds it. Both lists are the body's full arity, so the
                // two are aligned with each other and with the parameters, and a slot filled one way is
                // null in the other list.
                var arguments = new object?[slots.Length];
                StateRecipe?[]? recipes = null;

                for (var i = 0; i < slots.Length; i++)
                {
                    if (slots[i] is ParameterRecipe recipe)
                    {
                        recipes ??= new StateRecipe?[slots.Length];
                        recipes[i] = StateRecipe.For(recipe.Factory);
                    }
                    else
                    {
                        arguments[i] = slots[i];
                    }
                }

                expanded.Add(envelope with
                {
                    OriginalName = factory.Name,
                    ParameterSet = paramSet,
                    IsBaseline = false,

                    // Set here rather than inside each of the twelve Add overloads' factory lambdas:
                    // this is the one place that holds both the user's typed delegate and the values
                    // this expansion will call it with, so there is no second copy to drift.
                    Body = factory.Action,
                    Arguments = arguments,
                    StateRecipes = recipes,
                    IterationSetup = factory.IterationSetup,
                    IterationTeardown = factory.IterationTeardown,
                });
            }
        }

        if (expanded.Count == 0)
        {
            throw new InvalidOperationException(
                "No benchmarks matched the registered parameters. " +
                "Ensure the typed lambda parameters match the WithParameter type arguments.");
        }

        return [.. _benchmarks, .. expanded];
    }

    private void ApplyPerParameterSignificance(List<BenchmarkResult> results, Dictionary<string, double[]> rawSamples)
    {
        static string RawKey(BenchmarkResult r)
        {
            return RawSampleKey.For(r);
        }

        if (!results.Any(r => r.ParameterSet.Count > 0))
        {
            // Significance only makes sense within the same runtime; net8 vs net10 is not
            // a meaningful comparison for p-value purposes.
            foreach (var runtimeGroup in results.GroupBy(ComparisonGroup.KeyFor))
            {
                var runtimeList = runtimeGroup.ToList();
                var runtimeRaw = new Dictionary<string, double[]>();

                foreach (var r in runtimeList)
                {
                    if (rawSamples.TryGetValue(RawKey(r), out var samples))
                        runtimeRaw[r.Name] = samples;
                }

                var indices = results
                    .Select((res, idx) => (res, idx))
                    .Where(x => ComparisonGroup.KeyFor(x.res) == runtimeGroup.Key)
                    .Select(x => x.idx)
                    .ToList();

                Significance.ApplyIfEnabled(runtimeList, runtimeRaw, _options);

                for (var j = 0; j < runtimeList.Count; j++)
                {
                    results[indices[j]] = runtimeList[j];
                }
            }

            return;
        }

        var indexedResults = results
            .Select((r, idx) => (Result: r, Index: idx))
            .ToList();

        var groups = indexedResults
            .GroupBy(ri => BenchmarkParameter.GetKey(ri.Result.ParameterSet))
            .ToList();

        foreach (var group in groups)
        {
            foreach (var runtimeGroup in group.GroupBy(ri => ComparisonGroup.KeyFor(ri.Result)))
            {
                var groupList = runtimeGroup.ToList();
                var groupResults = groupList.Select(ri => ri.Result).ToList();
                var groupRaw = new Dictionary<string, double[]>();

                foreach (var ri in groupList)
                {
                    if (rawSamples.TryGetValue(RawKey(ri.Result), out var samples))
                        groupRaw[ri.Result.Name] = samples;
                }

                Significance.ApplyIfEnabled(groupResults, groupRaw, _options);

                for (var j = 0; j < groupList.Count; j++)
                {
                    results[groupList[j].Index] = groupResults[j];
                }
            }
        }
    }

    private List<object?[]> ComputeParameterCombinations()
    {
        var result = new List<object?[]>();
        result.Add([]);

        foreach (var def in _parameterDefs)
        {
            var next = new List<object?[]>();

            foreach (var existing in result)
            {
                foreach (var value in def.Values)
                {
                    var combined = new object?[existing.Length + 1];
                    Array.Copy(existing, combined, existing.Length);
                    combined[^1] = value;
                    next.Add(combined);
                }
            }

            result = next;
        }

        return result;
    }

    private static string FormatParamDisplayName(string benchmarkName, BenchmarkParameter[] paramSet)
        => BenchmarkParameter.FormatDisplayName(benchmarkName, paramSet);

    private async Task InvokeReportersAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
    {
        await ReporterRegistry.InvokeReportersAsync(_reporters, _detail, results, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool AreTypesCompatible(Type[] factoryTypes, Type[] parameterTypes)
    {
        if (factoryTypes.Length != parameterTypes.Length)
            return false;

        for (var i = 0; i < factoryTypes.Length; i++)
        {
            if (factoryTypes[i] != parameterTypes[i])
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Arranges a parameter sweep: parameter values stay in declaration order and the
    ///     benchmarks within each value are shuffled. A suite with no parameters is left alone here
    ///     and shuffled by <see cref="SuiteRunner" /> instead, which is where the run seed reaches.
    /// </summary>
    private List<BenchmarkEnvelope> ApplyExecutionOrder(IReadOnlyList<BenchmarkEnvelope> expanded, RunOrder order)
        => _parameterDefs.Count == 0
            ? [.. expanded]
            : RunOrdering.ApplyWithinGroups(expanded, order, _seed, e => BenchmarkParameter.GetKey(e.ParameterSet));

    // --- Validation ---

    /// <summary>
    ///     Refuses a parameter value that could not cross a process boundary intact.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The permitted set is <see cref="TestArgumentCodec.IsSupported" /> and nothing else. It used
    ///         to be a second, hand-written list here that rejected <see cref="DateTime" />,
    ///         <see cref="DateTimeOffset" />, <see cref="TimeSpan" />, <see cref="Guid" />,
    ///         <see cref="nint" /> and <see cref="nuint" /> - all of which the codec carries - so a sweep
    ///         over a <c>TimeSpan</c> was refused at registration for a limitation the wire does not have,
    ///         and the two lists could drift in the other direction just as easily.
    ///     </para>
    ///     <para>
    ///         Checked against the value's <b>runtime</b> type, which is the weaker of the two questions
    ///         and deliberately so: the strong one - can the <i>declared</i> type be encoded - belongs to
    ///         <see cref="BodyRef.TryCreate" />, which reads it from the body's own signature. A
    ///         <c>WithParameter&lt;object&gt;</c> sweep over boxed ints passes here and is refused there,
    ///         by name, because <c>object</c> is what the codec would have to produce.
    ///     </para>
    /// </remarks>
    private static void ValidateParameterType<T>(string name, T[] values)
    {
        foreach (var value in values)
        {
            if (value is not null && !TestArgumentCodec.IsSupported(value.GetType()))
            {
                throw new ArgumentException(
                    "Parameter values must be primitives, enums, strings, decimal, DateTime, "
                    + "DateTimeOffset, TimeSpan, Guid, or null. Value of type "
                    + $"'{value.GetType().FullName}' for parameter '{name}' is not supported.",
                    name);
            }
        }
    }

    private IReadOnlyList<BenchmarkEnvelope> ApplyCategoryFilter(IReadOnlyList<BenchmarkEnvelope> benchmarks)
    {
        if (_categoryFilterInclude.Count == 0 && _categoryFilterExclude.Count == 0)
            return benchmarks;

        return benchmarks
            .Where(b => CategoryFilter.Matches(b.Categories, _categoryFilterInclude, _categoryFilterExclude, _categoryFilterInclude.Count > 0))
            .ToList();
    }

    private static IReadOnlyList<string> ResolveAddCategories(
        IReadOnlyList<string>? explicitCategories,
        IReadOnlyList<string>? pendingCategories)
    {
        if (explicitCategories is not null)
            return NormalizeCategories(explicitCategories, "categories");

        if (pendingCategories is null)
            return [];

        return pendingCategories.ToArray();
    }

    private static string[] NormalizeCategories(IEnumerable<string> categories, string paramName)
    {
        var normalized = new List<string>();

        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category names cannot be null, empty, or whitespace.", paramName);

            var trimmed = category.Trim();

            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                normalized.Add(trimmed);
        }

        return [.. normalized];
    }

    private static void AddCategories(List<string> target, IEnumerable<string> source, string paramName)
    {
        foreach (var category in NormalizeCategories(source, paramName))
        {
            if (!target.Contains(category, StringComparer.OrdinalIgnoreCase))
                target.Add(category);
        }
    }

    private void ValidateBaseline()
    {
        if (_baselineName is null)
            return;

        var allNames = new HashSet<string>(_benchmarks.Select(b => b.Name));
        allNames.UnionWith(_parameterizedFactories.Select(f => f.Name));

        if (!allNames.Contains(_baselineName))
        {
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", allNames));
        }
    }

    private sealed record ParameterDef(string Name, Type Type, object?[] Values);

    /// <summary>
    ///     One value in a sweep that is built in the measuring process rather than sent to it.
    /// </summary>
    /// <remarks>
    ///     Occupies a slot in <see cref="ParameterDef.Values" /> in place of the value itself, so
    ///     combination-building and display-name formatting are unchanged - only the two places that
    ///     turn a slot into something usable know the difference:
    ///     <see cref="SlotValue{T}" /> for the in-process path and <see cref="ExpandEnvelopes" /> for
    ///     the addressed one.
    /// </remarks>
    private sealed record ParameterRecipe(string Label, Delegate Factory);

    /// <summary>
    ///     Resolves one expansion slot to the value the in-process path should measure over, running a
    ///     recipe if that is what the slot holds - once, and only if this benchmark is actually measured
    ///     here.
    /// </summary>
    /// <remarks>
    ///     Deferred rather than resolved during expansion, which happens before the isolation decision.
    ///     Building it there would run every recipe in the coordinator on every run, including the
    ///     isolated ones where the worker builds its own and this copy is measured by nobody - the same
    ///     mistake the host-side service-provider resolvers made until they were made lazy. The
    ///     resolution is outside the timed region either way; what runs inside the loop is still a
    ///     delegate over a plain local.
    /// </remarks>
    private static Func<T> SlotValue<T>(object? slot)
    {
        if (slot is not ParameterRecipe recipe)
            return () => (T)slot!;

        var built = false;
        T value = default!;

        return () =>
        {
            if (built)
                return value;

            // Always a Func<T>: WithParameter<T> is the only thing that builds a ParameterRecipe and
            // that is the shape it takes. Cast rather than DynamicInvoke so a failure names the type
            // rather than arriving wrapped in a TargetInvocationException.
            value = ((Func<T>)recipe.Factory)();
            built = true;

            return value;
        };
    }

    /// <param name="Action">
    ///     The user's own typed lambda, kept beside the factory that wraps it. The factory's own
    ///     metadata token identifies NBenchmark's wrapper; only this points at the method the developer
    ///     wrote, and it is what lets a parameter sweep be addressed for a worker.
    /// </param>
    /// <param name="IterationSetup">
    ///     The per-iteration <c>setup</c> this registration supplied, if any. Carried here because only
    ///     the registration knows, and the expansion is what builds the envelope addressing consults.
    ///     Before parameter sweeps were addressable this went unrecorded and was harmless - a
    ///     parameterized envelope carried no body, so it was refused for that reason first. It is
    ///     load-bearing now: an addressed body whose hooks were forgotten would be measured in a worker
    ///     with its setup silently dropped.
    /// </param>
    private sealed record ParameterizedAdd(
        string Name,
        IReadOnlyList<string> Categories,
        Func<object?[], string, BenchmarkEnvelope> Factory,
        Type[] ParamTypes,
        Delegate Action,
        Action? IterationSetup,
        Action? IterationTeardown);
}
