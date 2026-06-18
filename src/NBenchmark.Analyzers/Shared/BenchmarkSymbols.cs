using Microsoft.CodeAnalysis;

namespace NBenchmark.Analyzers.Shared;

internal static class BenchmarkSymbols
{
    private const string BenchmarkAttributeFullName = "NBenchmark.Attributes.BenchmarkAttribute";
    private const string BenchmarkCaseAttributeFullName = "NBenchmark.Attributes.BenchmarkCaseAttribute";
    private const string BenchmarkCasesAttributeFullName = "NBenchmark.Attributes.BenchmarkCasesAttribute";
    private const string BenchmarkSetupAttributeFullName = "NBenchmark.Attributes.BenchmarkSetupAttribute";
    private const string BenchmarkTeardownAttributeFullName = "NBenchmark.Attributes.BenchmarkTeardownAttribute";
    private const string BenchmarkIterationSetupAttributeFullName = "NBenchmark.Attributes.BenchmarkIterationSetupAttribute";
    private const string BenchmarkIterationTeardownAttributeFullName = "NBenchmark.Attributes.BenchmarkIterationTeardownAttribute";
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
            or BenchmarkIterationSetupAttributeFullName
            or BenchmarkIterationTeardownAttributeFullName;
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
