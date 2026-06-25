using System.Collections.Immutable;
using System.Globalization;
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
    private const string IStateResetTypeMetadataName = "NBenchmark.Lifecycle.IStateReset";
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

        if (ImplementsIStateReset(context.Compilation, type))
            return;

        var scopedParam = FindScopedConstructorParameter(type);

        if (scopedParam is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, typeDecl.Identifier.GetLocation(), type.Name, scopedParam.Type.Name));
    }

    private static bool ImplementsIStateReset(Compilation compilation, INamedTypeSymbol type)
    {
        var iStateResetType = compilation.GetTypeByMetadataName(IStateResetTypeMetadataName);

        if (iStateResetType is null)
            return false;

        for (var i = 0; i < type.AllInterfaces.Length; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(type.AllInterfaces[i], iStateResetType))
                return true;
        }

        return false;
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

        var ordinal = Convert.ToInt32(value, CultureInfo.InvariantCulture);

        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol field
                && field.Name == memberName
                && field.HasConstantValue
                && field.ConstantValue is int memberValue
                && memberValue == ordinal)
                return true;
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
    ///     Broad detection of types that may hold per-instance state when injected into a
    ///     PerClass benchmark. Flags any non-primitive, non-ambient reference-type
    ///     constructor parameter, excluding well-known stateless types
    ///     (<c>ILogger&lt;T&gt;</c>, <c>IOptions&lt;T&gt;</c>, etc.) and the ambient-type
    ///     allowlist. Users with intentional sharing can suppress the diagnostic with
    ///     <c>#pragma warning disable NB0011</c>.
    /// </summary>
    private static bool LooksLikeScopedService(ITypeSymbol type)
    {
        if (type.IsValueType)
            return false;

        if (type.SpecialType == SpecialType.System_String)
            return false;

        if (IsWellKnownAmbientType(type))
            return false;

        if (IsWellKnownStatelessType(type))
            return false;

        return type.IsReferenceType;
    }

    private static bool IsWellKnownStatelessType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var fullName = named.OriginalDefinition?.ToDisplayString(FullNameFormat)
                       ?? named.ToDisplayString(FullNameFormat);

        return fullName is
            "global::Microsoft.Extensions.Logging.ILogger<T>" or
            "global::Microsoft.Extensions.Options.IOptions<T>" or
            "global::Microsoft.Extensions.Options.IOptionsSnapshot<T>" or
            "global::Microsoft.Extensions.Options.IOptionsMonitor<T>";
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
