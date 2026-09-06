using Microsoft.CodeAnalysis;

namespace NBenchmark.Analyzers.Shared;

internal static class BenchmarkSymbols
{
    private const string BenchmarkAttributeFullName = "NBenchmark.BenchmarkAttribute";
    private const string BenchmarkCaseAttributeFullName = "NBenchmark.ArgumentsAttribute";
    private const string BenchmarkCasesAttributeFullName = "NBenchmark.ArgumentsSourceAttribute";
    private const string BenchmarkSetupAttributeFullName = "NBenchmark.GlobalSetupAttribute";
    private const string BenchmarkTeardownAttributeFullName = "NBenchmark.GlobalTeardownAttribute";
    private const string SampleSetupAttributeFullName = "NBenchmark.SampleSetupAttribute";
    private const string SampleTeardownAttributeFullName = "NBenchmark.SampleTeardownAttribute";
    private const string MeasurementOptionsFullName = "NBenchmark.MeasurementOptions";

    private static string? GetAttributeFullName(INamedTypeSymbol? attributeClass) =>
        attributeClass?.OriginalDefinition?.ToDisplayString() ?? attributeClass?.ToDisplayString();

    public static bool IsBenchmarkAttribute(INamedTypeSymbol? attributeClass)
    {
        var name = GetAttributeFullName(attributeClass);
        return name == BenchmarkAttributeFullName;
    }

    public static bool IsBenchmarkCaseAttribute(INamedTypeSymbol? attributeClass)
    {
        var name = GetAttributeFullName(attributeClass);
        return name == BenchmarkCaseAttributeFullName;
    }

    public static bool IsBenchmarkCasesAttribute(INamedTypeSymbol? attributeClass)
    {
        var name = GetAttributeFullName(attributeClass);
        return name == BenchmarkCasesAttributeFullName;
    }

    public static bool IsLifecycleAttribute(INamedTypeSymbol? attributeClass)
    {
        var name = GetAttributeFullName(attributeClass);

        return name is BenchmarkSetupAttributeFullName
            or BenchmarkTeardownAttributeFullName
            or SampleSetupAttributeFullName
            or SampleTeardownAttributeFullName;
    }

    public static bool HasBenchmarkAttribute(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (IsBenchmarkAttribute(attr.AttributeClass))
                return true;
        }

        return false;
    }

    public static bool HasBenchmarkAttribute(INamedTypeSymbol type)
    {
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (HasBenchmarkAttribute(method))
                return true;
        }

        return false;
    }

    public static bool HasDeclaredBenchmarkAttribute(INamedTypeSymbol type)
    {
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.ContainingType?.Equals(type, SymbolEqualityComparer.Default) != true)
                continue;

            if (HasBenchmarkAttribute(method))
                return true;
        }

        return false;
    }

    public static bool IsMeasurementOptionsType(INamedTypeSymbol? type)
    {
        var name = GetAttributeFullName(type);
        return name == MeasurementOptionsFullName;
    }
}
