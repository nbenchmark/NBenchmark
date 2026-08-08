using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

/// <summary>
///     NB0011 - a benchmark class whose methods share one instance, and one injected dependency, for
///     the whole class.
/// </summary>
/// <remarks>
///     <para>
///         PerClass is reachable two ways and the rule used to see only one of them. The class
///         attribute is local and obvious; <c>BenchmarkHarness.WithInstanceLifetime(PerClass)</c> is a
///         run-global default applying to every discovered class in the assembly, is supported and
///         tested, and produced exactly the same sharing with nothing said at compile time. The
///         fluent form is necessarily answered at compilation end, because the call that decides it
///         is usually in a different file from the class it decides for - and only within this
///         compilation, since a harness in a separate project is out of reach of any analyzer.
///     </para>
///     <para>
///         The second half is <c>IStateReset</c> being taken at its word. The engine
///         can only see that the interface is present, so an empty <c>ResetAsync</c> disables every
///         safeguard while resetting nothing - and until this shipped, the quick fix for this very
///         diagnostic offered exactly that body. A method body is the one thing an analyzer can read
///         and a runtime check cannot, so the empty implementation is reported here rather than
///         trusted.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerClassWithScopedServiceAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The <c>[InstanceLifetime]</c> attribute value, or its absence.</summary>
    private enum DeclaredLifetime
    {
        /// <summary>No attribute - the harness default decides, so this class waits for the scan.</summary>
        Unspecified,
        PerClass,
        PerMethod,
    }

    /// <summary>What the class's <c>ResetAsync</c> actually does.</summary>
    private enum ResetKind
    {
        /// <summary>The class does not implement the interface.</summary>
        None,

        /// <summary>Implemented with a body that does something.</summary>
        Real,

        /// <summary>Implemented as <c>return Task.CompletedTask;</c> and nothing else.</summary>
        NoOp,
    }

    private const string InstanceLifetimeTypeMetadataName = "NBenchmark.InstanceLifetime";
    private const string IStateResetTypeMetadataName = "NBenchmark.Lifecycle.IStateReset";
    private const string SharedStateAttributeMetadataName = "NBenchmark.Attributes.SharedStateAttribute";
    private const string PerClassMemberName = "PerClass";
    private const string PerMethodMemberName = "PerMethod";
    private const string WithInstanceLifetimeMethodName = "WithInstanceLifetime";
    private const string ResetMethodName = "ResetAsync";

    private const string AttributeSource = "[InstanceLifetime(InstanceLifetime.PerClass)]";
    private const string FluentSource = "WithInstanceLifetime(InstanceLifetime.PerClass)";

    /// <remarks>
    ///     The remedy is the last format argument rather than a second descriptor, because a class
    ///     whose <c>ResetAsync</c> is empty and one that never implemented the interface have the
    ///     same defect and want different next steps - and one id with one severity keeps a single
    ///     <c>#pragma warning disable NB0011</c> covering both.
    /// </remarks>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.PerClassWithScopedService,
        "PerClass instance lifetime with scoped service may cause state contamination",
        "Class '{0}' shares one instance across its [Benchmark] methods ({1}) and takes '{2}' as a constructor dependency, so the second method can observe state the first left behind - which breaks the statistical-independence assumption of the significance test. {3}.",
        "NBenchmark.Usage",
        DiagnosticSeverity.Warning,
        true);

    private const string MissingResetRemedy =
        "Use InstanceLifetime.PerMethod, implement IStateReset to reset between methods, or declare "
        + "the sharing with [SharedState]";

    private const string EmptyResetRemedy =
        "Its IStateReset.ResetAsync body is empty, so implementing the interface silenced this "
        + "without resetting anything - the engine can only see that the interface is present. Reset "
        + "what the methods share, or declare the carry-over with [SharedState] instead";

    private static readonly SymbolDisplayFormat FullNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var scan = new FluentScan();

            start.RegisterSyntaxNodeAction(
                c => AnalyzeType(c, scan), SyntaxKind.ClassDeclaration, SyntaxKind.RecordDeclaration);

            start.RegisterSyntaxNodeAction(
                c => AnalyzeInvocation(c, scan), SyntaxKind.InvocationExpression);

            start.RegisterCompilationEndAction(c => ReportDeferred(c, scan));
        });
    }

    /// <summary>
    ///     What the whole-compilation pass collected: whether the harness default is PerClass, and
    ///     the classes whose verdict depends on the answer.
    /// </summary>
    private sealed class FluentScan
    {
        private int _perClassDefault;

        public ConcurrentBag<Deferred> Deferred { get; } = [];

        public bool PerClassDefault => Volatile.Read(ref _perClassDefault) != 0;

        public void MarkPerClassDefault() => Volatile.Write(ref _perClassDefault, 1);
    }

    private sealed class Deferred(Location location, string className, string dependencyName, ResetKind reset)
    {
        public Location Location { get; } = location;

        public string ClassName { get; } = className;

        public string DependencyName { get; } = dependencyName;

        public ResetKind Reset { get; } = reset;
    }

    private static void AnalyzeType(SyntaxNodeAnalysisContext context, FluentScan scan)
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

        // One method cannot contaminate itself. The rule read the lifetime and the dependency but
        // never the method count, so it fired on classes where the failure it describes - the second
        // method observing the first's leftovers - has no second method to happen to.
        if (DeclaredBenchmarkCount(type) < 2)
            return;

        // The author has said the sharing is the point. Unlike an empty ResetAsync, this claims
        // nothing that a body could contradict.
        if (SharesIntentionally(context.Compilation, type))
            return;

        var scopedParam = FindScopedConstructorParameter(type);

        if (scopedParam is null)
            return;

        var reset = ClassifyReset(context.Compilation, type, context.CancellationToken);

        if (reset == ResetKind.Real)
            return;

        var lifetime = ReadDeclaredLifetime(context.Compilation, type);

        if (lifetime == DeclaredLifetime.PerMethod)
            return;

        var location = typeDecl.Identifier.GetLocation();

        // No attribute: the harness default decides, and the call that sets it is somewhere else in
        // the compilation - possibly in a file this action has not visited yet.
        if (lifetime == DeclaredLifetime.Unspecified)
        {
            scan.Deferred.Add(new Deferred(location, type.Name, scopedParam.Type.Name, reset));

            return;
        }

        context.ReportDiagnostic(Describe(location, type.Name, AttributeSource, scopedParam.Type.Name, reset));
    }

    /// <summary>
    ///     Records a <c>WithInstanceLifetime(InstanceLifetime.PerClass)</c> call anywhere in the
    ///     compilation.
    /// </summary>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, FluentScan scan)
    {
        if (scan.PerClassDefault)
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;

        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };

        if (name != WithInstanceLifetimeMethodName)
            return;

        var instanceLifetimeType = context.Compilation.GetTypeByMetadataName(InstanceLifetimeTypeMetadataName);

        if (instanceLifetimeType is null)
            return;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (context.SemanticModel.GetSymbolInfo(argument.Expression, context.CancellationToken).Symbol
                is not IFieldSymbol { ContainingType: { } containing } field)
                continue;

            if (field.Name != PerClassMemberName)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(containing, instanceLifetimeType))
                continue;

            scan.MarkPerClassDefault();

            return;
        }
    }

    private static void ReportDeferred(CompilationAnalysisContext context, FluentScan scan)
    {
        if (!scan.PerClassDefault)
            return;

        foreach (var deferred in scan.Deferred)
        {
            context.ReportDiagnostic(
                Describe(deferred.Location, deferred.ClassName, FluentSource, deferred.DependencyName, deferred.Reset));
        }
    }

    private static Diagnostic Describe(
        Location location,
        string className,
        string lifetimeSource,
        string dependencyName,
        ResetKind reset)
        => Diagnostic.Create(
            Rule,
            location,
            className,
            lifetimeSource,
            dependencyName,
            reset == ResetKind.NoOp ? EmptyResetRemedy : MissingResetRemedy);

    private static int DeclaredBenchmarkCount(INamedTypeSymbol type)
    {
        var count = 0;

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.ContainingType?.Equals(type, SymbolEqualityComparer.Default) != true)
                continue;

            if (BenchmarkSymbols.HasBenchmarkAttribute(method))
                count++;
        }

        return count;
    }

    private static bool SharesIntentionally(Compilation compilation, INamedTypeSymbol type)
    {
        var attributeType = compilation.GetTypeByMetadataName(SharedStateAttributeMetadataName);

        if (attributeType is null)
            return false;

        foreach (var attribute in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                continue;

            // Mirrors the runtime rule: the property defaults to true, so the bare attribute counts,
            // and an explicit false parks the attribute without suppressing anything.
            var intentional = attribute.NamedArguments
                .FirstOrDefault(a => a.Key == "Intentional").Value.Value;

            if (intentional is not false)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Whether the class implements <c>IStateReset</c> and, if so, whether its implementation
    ///     does anything.
    /// </summary>
    private static ResetKind ClassifyReset(
        Compilation compilation,
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        var iStateResetType = compilation.GetTypeByMetadataName(IStateResetTypeMetadataName);

        if (iStateResetType is null)
            return ResetKind.None;

        var implemented = false;

        for (var i = 0; i < type.AllInterfaces.Length; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(type.AllInterfaces[i], iStateResetType))
            {
                implemented = true;

                break;
            }
        }

        if (!implemented)
            return ResetKind.None;

        if (iStateResetType.GetMembers(ResetMethodName).OfType<IMethodSymbol>().FirstOrDefault()
            is not { } interfaceMethod)
            return ResetKind.Real;

        if (type.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation)
            return ResetKind.Real;

        // Inherited from a referenced assembly: no body to read, so nothing to accuse it of.
        if (implementation.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference)
            return ResetKind.Real;

        return reference.GetSyntax(cancellationToken) is MethodDeclarationSyntax declaration
               && IsEmptyBody(declaration)
            ? ResetKind.NoOp
            : ResetKind.Real;
    }

    /// <summary>
    ///     Whether a <c>ResetAsync</c> declaration resets nothing: an expression body returning a
    ///     completed task, an empty block, or a block whose only statement returns one.
    /// </summary>
    private static bool IsEmptyBody(MethodDeclarationSyntax declaration)
    {
        if (declaration.ExpressionBody is { } expressionBody)
            return IsCompletedTask(expressionBody.Expression);

        if (declaration.Body is not { } block)
            return false;

        // `async Task ResetAsync(...) { }` - awaits nothing, resets nothing.
        if (block.Statements.Count == 0)
            return true;

        return block.Statements.Count == 1
               && block.Statements[0] is ReturnStatementSyntax { Expression: { } returned }
               && IsCompletedTask(returned);
    }

    private static bool IsCompletedTask(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "CompletedTask",
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.DefaultLiteralExpression)
                                           || literal.IsKind(SyntaxKind.NullLiteralExpression),
        DefaultExpressionSyntax => true,
        _ => false,
    };

    /// <summary>
    ///     The lifetime the class's own <c>[InstanceLifetime]</c> attribute declares. The value is
    ///     resolved through the compilation's <c>NBenchmark.InstanceLifetime</c> enum symbol so that
    ///     reordering members in the enum does not break the analyzer.
    /// </summary>
    private static DeclaredLifetime ReadDeclaredLifetime(Compilation compilation, INamedTypeSymbol type)
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
                return DeclaredLifetime.PerClass;

            if (TryMatchEnumValueByName(instanceLifetimeType, arg.Value, PerMethodMemberName))
                return DeclaredLifetime.PerMethod;
        }

        return DeclaredLifetime.Unspecified;
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

    /// <remarks>
    ///     Non-public constructors are inspected too. A container resolves an internal constructor
    ///     perfectly well, so skipping them meant the shape most likely to be DI-only - an internal
    ///     type wired up by a factory - was the one shape never checked. Implicitly declared
    ///     constructors are skipped, because a record's compiler-generated copy constructor takes the
    ///     record itself as a reference-type parameter and would flag every PerClass record for
    ///     depending on itself.
    /// </remarks>
    private static IParameterSymbol? FindScopedConstructorParameter(INamedTypeSymbol type)
    {
        foreach (var ctor in type.Constructors)
        {
            if (ctor.IsStatic || ctor.IsImplicitlyDeclared)
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
    ///     (<c>ILogger</c>, <c>IOptions&lt;T&gt;</c>, etc.) and the ambient-type
    ///     allowlist. Users with intentional sharing can declare it with <c>[SharedState]</c> or
    ///     suppress the diagnostic with <c>#pragma warning disable NB0011</c>.
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
            // The non-generic forms were missing, so `ILogger` and `ILoggerFactory` - which are as
            // stateless as the generic one, and are what a class not tied to one category injects -
            // were reported as suspected scoped services.
            "global::Microsoft.Extensions.Logging.ILogger" or
            "global::Microsoft.Extensions.Logging.ILogger<T>" or
            "global::Microsoft.Extensions.Logging.ILoggerFactory" or
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
