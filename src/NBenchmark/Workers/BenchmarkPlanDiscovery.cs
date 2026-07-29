using System.Reflection;
using NBenchmark.Attributes;

namespace NBenchmark.Workers;

/// <summary>
///     Finds <see cref="BenchmarkPlanAttribute" />-marked factories on a type and binds them as
///     delegates the worker can address.
/// </summary>
internal static class BenchmarkPlanDiscovery
{
    /// <summary>
    ///     Every plan on <paramref name="declaringType" />, in declaration order.
    /// </summary>
    /// <remarks>
    ///     A method marked as a plan but shaped wrongly - not static, taking parameters, or returning
    ///     something other than a <see cref="BenchmarkSuite" /> - throws rather than being skipped.
    ///     Silently ignoring it would leave the author with a benchmark suite that simply never ran
    ///     and no indication why, which is the failure mode this whole area exists to avoid.
    /// </remarks>
    public static IReadOnlyList<Func<BenchmarkSuite>> Find(Type declaringType)
    {
        ArgumentNullException.ThrowIfNull(declaringType);

        var plans = new List<Func<BenchmarkSuite>>();

        var candidates = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.IsDefined(typeof(BenchmarkPlanAttribute), inherit: false))
            .OrderBy(m => m.MetadataToken);

        foreach (var method in candidates)
        {
            if (!method.IsStatic)
            {
                throw new InvalidOperationException(
                    $"'{declaringType.Name}.{method.Name}' is marked [BenchmarkPlan] but is not static. "
                    + "A plan must be static, because a worker builds the suite in its own process and "
                    + "has no instance of your type to call it on.");
            }

            if (method.GetParameters().Length != 0)
            {
                throw new InvalidOperationException(
                    $"'{declaringType.Name}.{method.Name}' is marked [BenchmarkPlan] but takes "
                    + $"{method.GetParameters().Length} parameter(s). A plan must be parameterless.");
            }

            if (!typeof(BenchmarkSuite).IsAssignableFrom(method.ReturnType))
            {
                throw new InvalidOperationException(
                    $"'{declaringType.Name}.{method.Name}' is marked [BenchmarkPlan] but returns "
                    + $"{method.ReturnType.Name} rather than {nameof(BenchmarkSuite)}.");
            }

            plans.Add(method.CreateDelegate<Func<BenchmarkSuite>>());
        }

        return plans;
    }
}
