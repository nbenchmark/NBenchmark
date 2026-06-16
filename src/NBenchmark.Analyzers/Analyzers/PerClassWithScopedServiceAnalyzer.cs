using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerClassWithScopedServiceAnalyzer : DiagnosticAnalyzer
{
    private const string InstanceLifetimeTypeMetadataName = "NBenchmark.InstanceLifetime";
    private const string PerClassMemberName = "PerClass";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.PerClassWithScopedService,
        "PerClass instance lifetime with scoped service may cause state contamination",
        "Class '{0}' uses [InstanceLifetime(PerClass)] and injects '{1}', which looks like a scoped service. Sharing a single instance across all [Benchmark] methods in the suite can cause the second method to observe cached state from the first. Consider [InstanceLifetime(PerMethod)] unless you have a specific reason to share state.",
        "NBenchmark.Usage",
        DiagnosticSeverity.Warning,
        true);

    private static readonly SymbolDisplayFormat FullNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.ClassDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.RecordDeclaration);
    }

    private static void AnalyzeType(SyntaxNodeAnalysisContext context)
    {
        TypeDeclarationSyntax? typeDecl = context.Node switch
        {
            ClassDeclarationSyntax c => c,
            RecordDeclarationSyntax r => r,
            _ => null,
        };

        if (typeDecl is null)
            return;

        if (typeDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return;

        var type = context.SemanticModel.GetDeclaredSymbol(typeDecl);

        if (type is null)
            return;

        if (!BenchmarkSymbols.HasDeclaredBenchmarkAttribute(type))
            return;

        if (!HasPerClassLifetime(context.Compilation, type))
            return;

        var scopedParam = FindScopedConstructorParameter(type);

        if (scopedParam is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, typeDecl.Identifier.GetLocation(), type.Name, scopedParam.Type.Name));
    }

    /// <summary>
    ///     True when the type carries <c>[InstanceLifetime(InstanceLifetime.PerClass)]</c>.
    ///     The lifetime value is resolved through the compilation's <c>NBenchmark.InstanceLifetime</c>
    ///     enum symbol so that reordering members in the enum does not break the analyzer.
    /// </summary>
    private static bool HasPerClassLifetime(Compilation compilation, INamedTypeSymbol type)
    {
        var instanceLifetimeType = compilation.GetTypeByMetadataName(InstanceLifetimeTypeMetadataName);

        foreach (var attr in type.GetAttributes())
        {
            if (!IsInstanceLifetimeAttribute(attr))
                continue;

            if (attr.ConstructorArguments.Length != 1)
                continue;

            var arg = attr.ConstructorArguments[0];

            if (instanceLifetimeType is null || arg.Type is null)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(arg.Type, instanceLifetimeType))
                continue;

            if (TryMatchEnumValueByName(instanceLifetimeType, arg.Value, PerClassMemberName))
                return true;
        }

        return false;
    }

    private static bool IsInstanceLifetimeAttribute(AttributeData attr)
    {
        var original = attr.AttributeClass?.OriginalDefinition;
        if (original is null)
            return false;

        if (original.MetadataName != "InstanceLifetimeAttribute")
            return false;

        if (original.ContainingType is not null)
            return false;

        var ns = original.ContainingNamespace;
        return ns is { IsGlobalNamespace: false }
               && ns.ToDisplayString() == "NBenchmark.Attributes";
    }

    private static bool TryMatchEnumValueByName(INamedTypeSymbol enumType, object? value, string memberName)
    {
        if (value is null)
            return false;

        if (value is INamedTypeSymbol namedMember)
            return namedMember.Name == memberName;

        var ordinal = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol field
                && field.Name == memberName
                && field.HasConstantValue
                && field.ConstantValue is int memberValue
                && memberValue == ordinal)
            {
                return true;
            }
        }

        return false;
    }

    private static IParameterSymbol? FindScopedConstructorParameter(INamedTypeSymbol type)
    {
        foreach (var ctor in type.Constructors)
        {
            if (ctor.IsStatic || ctor.DeclaredAccessibility != Accessibility.Public)
                continue;

            foreach (var param in ctor.Parameters)
            {
                if (LooksLikeScopedService(param.Type))
                    return param;
            }
        }

        return null;
    }

    /// <summary>
    ///     Heuristic detection of types that look like scoped DI services. The check is
    ///     intentionally conservative: it requires the type to either (a) have a name
    ///     that strongly suggests a stateful disposable unit of work, AND not be a
    ///     well-known ambient type (e.g. <c>HttpContext</c>) that is almost never
    ///     what the user meant, or (b) implement the <c>System.IDisposable</c> or
    ///     <c>System.IAsyncDisposable</c> contract directly. The name-based net is
    ///     narrower than v1 to reduce false positives; users with custom naming
    ///     conventions can suppress the diagnostic with <c>#pragma warning disable
    ///     NB0011</c>.
    /// </summary>
    private static bool LooksLikeScopedService(ITypeSymbol type)
    {
        if (IsWellKnownAmbientType(type))
            return false;

        if (ImplementsDisposable(type))
            return true;

        var name = type.Name;

        return name.EndsWith("DbContext", StringComparison.Ordinal)
               || name.EndsWith("UnitOfWork", StringComparison.Ordinal);
    }

    private static bool ImplementsDisposable(ITypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.ToDisplayString(FullNameFormat) is "System.IDisposable" or "System.IAsyncDisposable")
                return true;
        }

        return false;
    }

    private static bool IsWellKnownAmbientType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var fullName = named.OriginalDefinition?.ToDisplayString(FullNameFormat) ?? named.ToDisplayString(FullNameFormat);

            return fullName is
                "global::Microsoft.AspNetCore.Http.HttpContext" or
                "global::Microsoft.AspNetCore.Http.IHttpContextAccessor" or
                "global::System.IServiceProvider" or
                "global::System.Threading.CancellationToken" or
                "global::System.Web.HttpContext";
        }

        return false;
    }
}
