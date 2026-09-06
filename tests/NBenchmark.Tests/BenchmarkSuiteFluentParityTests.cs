using System.Reflection;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Every fluent method on <see cref="BenchmarkSuite" /> is re-declared on
///     <see cref="BenchmarkSuite{TState}" />, so a chain over prepared state never decays to the base.
/// </summary>
/// <remarks>
///     <para>
///         The failure this prevents has no diagnostic. <c>BenchmarkSuite&lt;TState&gt;</c> exists to
///         keep <c>Add(string, Action&lt;TState&gt;)</c> in scope; calling any base method that was not
///         re-declared returns <c>BenchmarkSuite</c>, at which point the typed <c>Add</c> is gone and
///         the lambda parameter can no longer be inferred. The compiler reports that as a failure to
///         infer, several lines later, on a line that is not the one at fault.
///     </para>
///     <para>
///         Sixteen of the then-forty were declared when this was written by hand, so two thirds of the
///         surface decayed. A test rather than a convention, because the list grows every time the base
///         does and nothing else would notice.
///     </para>
/// </remarks>
public class BenchmarkSuiteFluentParityTests
{
    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;

    /// <summary>
    ///     Parameterized suites are a different mode: <c>WithParameter</c> types the body's parameter
    ///     the way prepared state does, and a suite cannot be both. These are excluded deliberately
    ///     rather than missed.
    /// </summary>
    private static readonly HashSet<string> NotApplicable = ["WithParameter"];

    [Fact]
    public void Every_Fluent_Method_On_The_Base_Is_Redeclared_On_The_Stateful_Suite()
    {
        var expected = typeof(BenchmarkSuite)
            .GetMethods(Declared)
            .Where(IsFluent)
            .Where(m => m.ReturnType == typeof(BenchmarkSuite))
            .Where(m => !NotApplicable.Contains(m.Name))
            .Select(Signature)
            .ToHashSet(StringComparer.Ordinal);

        var actual = typeof(BenchmarkSuite<>)
            .GetMethods(Declared)
            .Where(IsFluent)
            .Select(Signature)
            .ToHashSet(StringComparer.Ordinal);

        var missing = expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"BenchmarkSuite<TState> does not re-declare {missing.Count} of the base's fluent methods, "
            + "so a chain that calls one of them loses the typed Add with no diagnostic:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing.Select(m => "  " + m)));
    }

    /// <summary>
    ///     Each re-declaration returns the stateful suite. One that returned the base would satisfy the
    ///     name-and-arity check above while doing nothing this type exists for.
    /// </summary>
    [Fact]
    public void Every_Redeclared_Method_Returns_The_Stateful_Suite()
    {
        var wrong = typeof(BenchmarkSuite<>)
            .GetMethods(Declared)
            .Where(IsFluent)
            .Where(m => m.ReturnType != typeof(BenchmarkSuite<>).GetGenericArguments()[0].DeclaringType)
            .Select(Signature)
            .ToList();

        Assert.True(
            wrong.Count == 0,
            "these return the base rather than BenchmarkSuite<TState>:" + Environment.NewLine
            + string.Join(Environment.NewLine, wrong.Select(m => "  " + m)));
    }

    /// <summary>
    ///     The chainable configuration surface: every <c>With*</c> setter plus <c>Configure</c>, which
    ///     is one of them in everything but name.
    /// </summary>
    private static bool IsFluent(MethodInfo method)
        => method.Name.StartsWith("With", StringComparison.Ordinal) || method.Name == "Configure";

    /// <summary>Name plus parameter types, which is what makes an overload distinct.</summary>
    private static string Signature(MethodInfo method)
        => $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
}
