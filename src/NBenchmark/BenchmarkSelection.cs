namespace NBenchmark;

/// <summary>
///     A stable identity for a single discovered benchmark, used to drive exact selection of a
///     subset of benchmarks - for example from a checkbox picker - through
///     <see cref="BenchmarkHarness.WithSelection" />. The identity is the declaring type's full
///     name plus the discovered display name; parameter-expanded cases are distinguished by their
///     expanded display names, so each case is selectable independently.
/// </summary>
/// <param name="DeclaringTypeFullName">
///     The full name (namespace and type name) of the class that declares the benchmark, matching
///     <see cref="System.Type.FullName" /> of the discovered suite type.
/// </param>
/// <param name="DisplayName">
///     The discovered display name of the benchmark. For a parameterised benchmark this is the
///     expanded <c>Method(arg1, arg2, ...)</c> form.
/// </param>
public sealed record BenchmarkSelection(string DeclaringTypeFullName, string DisplayName);
