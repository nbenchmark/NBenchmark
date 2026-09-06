using System.Reflection;
using NBenchmark;
using NBenchmark.Lifecycle;

namespace NBenchmark.Engine;

/// <summary>
///     Decides how long a benchmark instance lives, and says so when the answer is not the one the
///     class asked for.
/// </summary>
/// <remarks>
///     <para>
///         Lifetime and isolation granularity used to be decided by one function, and the isolation
///         half shadowed the lifetime half: the global in-process switch returned before the
///         PerClass rule could run, and the rule itself was only reachable for harnesses that had
///         <i>no</i> instance source - so the safety net fired exactly where dependence was
///         impossible and never where a container was handing out instances. They are separate
///         questions. "How long does this object live" is answered here, from facts about the class;
///         "which process measures it" is answered by the harness, from facts about the run. Deciding
///         the first independently of the second is the whole point - a lifetime rule that can be
///         swallowed by an unrelated <c>--in-process</c> flag is not a rule.
///     </para>
///     <para>
///         The dependence this guards against is not hypothetical. A significance test assumes its
///         two samples are independent; two methods sharing one instance - and, under scoped DI, one
///         <c>DbContext</c> with a warm change tracker - produce a stable difference that a
///         Mann-Whitney U test over thousands of pooled samples will call significant essentially
///         every time.
///     </para>
/// </remarks>
internal static class InstanceIndependence
{
    /// <summary>
    ///     The lifetime a class actually runs under, given the lifetime it declared and where its
    ///     instances come from.
    /// </summary>
    /// <param name="type">The benchmark class.</param>
    /// <param name="declared">
    ///     The lifetime discovery resolved from the class attribute or the harness default.
    /// </param>
    /// <param name="factoryResolvedInstances">
    ///     Whether instances come from a user factory or a container rather than from the type's own
    ///     constructor. A container-resolved instance carries a scope with it, so sharing the
    ///     instance shares the scope - which is the case the whole rule exists for.
    /// </param>
    /// <param name="downgrade">
    ///     Why the declared lifetime was not honoured, or <c>null</c> when it was.
    /// </param>
    public static InstanceLifetime ResolveLifetime(
        Type type,
        InstanceLifetime declared,
        bool factoryResolvedInstances,
        out string? downgrade)
    {
        ArgumentNullException.ThrowIfNull(type);

        downgrade = null;

        if (declared != InstanceLifetime.PerClass)
            return InstanceLifetime.PerMethod;

        // The class answers for itself, in either direction: it resets between methods, or it says
        // the carry-over is the thing being measured. Both are honoured; neither is inferred.
        if (ResetsItself(type) || SharesIntentionally(type))
            return InstanceLifetime.PerClass;

        if (!factoryResolvedInstances)
            return InstanceLifetime.PerClass;

        downgrade =
            $"Class '{type.Name}' declares InstanceLifetime.PerClass and its instances come from a "
            + "factory or service container, so one instance - and, under scoped DI, one scope and "
            + "everything it holds - would be shared by every [Benchmark] method. It is measured with "
            + "a fresh instance per method instead, because the significance test assumes the methods "
            + "are independent. Implement IStateReset to keep PerClass and reset between methods, or "
            + "add [SharedState] to declare that the carry-over is deliberate.";

        return InstanceLifetime.PerMethod;
    }

    /// <summary>
    ///     The warning a genuinely shared instance carries, or <c>null</c> when nothing is shared or
    ///     the sharing was declared.
    /// </summary>
    /// <remarks>
    ///     Emitted from the one place both measuring processes reach, rather than from the
    ///     coordinator's in-process path alone - which is where it lived, so the default Harness path
    ///     (a worker measuring a PerClass class) produced the contamination with nothing said about
    ///     it at all.
    /// </remarks>
    public static string? DependenceWarning(
        Type type,
        InstanceLifetime lifetime,
        int benchmarkCount,
        MeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);

        if (options.SuppressedWarnings.HasFlag(BenchmarkWarnings.PerClassIndependence))
            return null;

        if (lifetime != InstanceLifetime.PerClass)
            return null;

        // One method cannot contaminate itself, and the launch loop rebuilds the instance between
        // launches, so a single-method class has nothing to warn about.
        if (benchmarkCount <= 1)
            return null;

        // A class that resets between methods, or that has said the sharing is the point, has
        // answered this already.
        if (ResetsItself(type) || SharesIntentionally(type))
            return null;

        return $"Class '{type.Name}' uses InstanceLifetime.PerClass with {benchmarkCount} [Benchmark] "
               + "methods. Sharing a single instance across methods can cause the second method to "
               + "observe cached state from the first, violating the statistical-independence "
               + "assumption of the significance test. To preserve independence: implement IStateReset "
               + "on the class (the engine will call it between methods), or use "
               + "InstanceLifetime.PerMethod. If the carry-over is deliberate, say so with "
               + "[SharedState] - or add BenchmarkWarnings.PerClassIndependence to "
               + "MeasurementOptions.SuppressedWarnings to "
               + "silence it for the whole run.";
    }

    /// <summary>Attaches <paramref name="warning" /> to every result in the list.</summary>
    public static void Attach(List<BenchmarkResult> results, string? warning)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (warning is null)
            return;

        for (var i = 0; i < results.Count; i++)
        {
            results[i] = results[i] with
            {
                Warnings = results[i].Warnings.Count > 0
                    ? [.. results[i].Warnings, warning]
                    : [warning],
            };
        }
    }

    /// <summary>
    ///     Whether the class implements <see cref="IStateReset" />.
    /// </summary>
    /// <remarks>
    ///     Presence, not behaviour - a runtime check cannot read a method body. Analyzer NB0011 does
    ///     the half this cannot: it reports an implementation that is only
    ///     <c>return Task.CompletedTask;</c>, which is the shape that used to buy silence for free.
    /// </remarks>
    public static bool ResetsItself(Type type) => typeof(IStateReset).IsAssignableFrom(type);

    /// <summary>Whether the class carries <c>[SharedState]</c>.</summary>
    public static bool SharesIntentionally(Type type)
        => type.GetCustomAttribute<SharedStateAttribute>() is { Acknowledged: true };
}
