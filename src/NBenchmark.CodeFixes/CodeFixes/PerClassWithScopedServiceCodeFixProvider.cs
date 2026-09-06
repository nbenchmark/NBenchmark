using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.CodeFixes.CodeFixes;

/// <summary>
///     Code fix provider for NB0011 (<c>PerClassWithScopedServiceAnalyzer</c>). Offers two fixes:
///     (1) change <c>[InstanceLifetime(PerClass)]</c> to <c>[InstanceLifetime(PerMethod)]</c> on
///     the class declaration, which gives each [Benchmark] method a fresh instance; (2) implement
///     <c>IStateReset</c> on the class so the engine can reset shared state between methods and
///     keep the <c>PerClass</c> lifetime. Fix (2) is offered only when the
///     <c>NBenchmark.Lifecycle.IStateReset</c> type is resolvable in the current compilation, and
///     it emits a body that must be written rather than one that already compiles - see
///     <see cref="ImplementIStateResetAsync" />.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp)]
[Shared]
public sealed class PerClassWithScopedServiceCodeFixProvider : CodeFixProvider
{
    private const string InstanceLifetimeMetadataName = "NBenchmark.InstanceLifetime";
    private const string PerClassMemberName = "PerClass";
    private const string PerMethodMemberName = "PerMethod";
    private const string IStateResetMetadataName = "NBenchmark.Lifecycle.IStateReset";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.PerClassWithScopedService);

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null)
            return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var typeDecl = root.FindToken(diagnosticSpan.Start).Parent?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeDecl is null)
            return;

        // Fix (1): change PerClass -> PerMethod on the [InstanceLifetime] attribute.
        var perMethodAttribute = TryFindPerClassInstanceLifetimeAttribute(typeDecl);

        if (perMethodAttribute is not null)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use InstanceLifetime.PerMethod",
                    cancellationToken => ChangeToPerMethodAsync(context.Document, typeDecl, perMethodAttribute, cancellationToken),
                    "UsePerMethod"),
                diagnostic);
        }

        // Fix (2): implement IStateReset on the class (only when the interface is resolvable).
        var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
        var iStateReset = compilation?.GetTypeByMetadataName(IStateResetMetadataName);

        if (iStateReset is not null)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Implement IStateReset",
                    cancellationToken => ImplementIStateResetAsync(context.Document, typeDecl, iStateReset, cancellationToken),
                    "ImplementIStateReset"),
                diagnostic);
        }
    }

    /// <summary>
    ///     Locates the <c>[InstanceLifetime(InstanceLifetime.PerClass)]</c> attribute on the
    ///     declaration so fix (1) can rewrite its argument. Returns the attribute syntax or
    ///     <c>null</c> when the attribute is not present (in which case fix (1) is suppressed).
    /// </summary>
    private static AttributeSyntax? TryFindPerClassInstanceLifetimeAttribute(TypeDeclarationSyntax typeDecl)
    {
        foreach (var attrList in typeDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();

                if (name is "InstanceLifetime" or "InstanceLifetimeAttribute")
                {
                    // Confirm the argument is the PerClass enum member by textual match. The
                    // analyzer has already validated this semantically; the code fix only runs
                    // on a diagnostic the analyzer emitted, so a textual check is sufficient.
                    var argText = attr.ArgumentList?.Arguments.FirstOrDefault()?.ToString();

                    if (argText is "InstanceLifetime.PerClass" or "PerClass")
                        return attr;
                }
            }
        }

        return null;
    }

    private static async Task<Document> ChangeToPerMethodAsync(
        Document document,
        TypeDeclarationSyntax typeDecl,
        AttributeSyntax attribute,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (root is null)
            return document;

        var oldArg = attribute.ArgumentList?.Arguments.FirstOrDefault();

        if (oldArg is null)
            return document;

        // Preserve the qualified "InstanceLifetime.PerMethod" form when the source used it,
        // otherwise use the unqualified "PerMethod" form the source used.
        var usesQualified = oldArg.ToString().Contains('.');
        var newText = usesQualified ? "InstanceLifetime.PerMethod" : PerMethodMemberName;
        var newArg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(newText));
        var newArgList = attribute.ArgumentList!.WithArguments(SyntaxFactory.SingletonSeparatedList(newArg));
        var newAttribute = attribute.WithArgumentList(newArgList);
        var newRoot = root.ReplaceNode(attribute, newAttribute);

        return document.WithSyntaxRoot(newRoot);
    }

    private static async Task<Document> ImplementIStateResetAsync(
        Document document,
        TypeDeclarationSyntax typeDecl,
        INamedTypeSymbol iStateReset,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (root is null)
            return document;

        var fullIStateResetName = iStateReset.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // A TODO plus a throw, not `return Task.CompletedTask;`. The generated body used to be the
        // completed-task form, which meant accepting this fix and not editing it silenced the
        // diagnostic and the engine's own PerClass safeguard while resetting nothing at all - the
        // fastest available route to the contamination the diagnostic exists to report. A body that
        // does not compile away quietly is the point: the author has to say what resetting means for
        // their class, which is the one thing neither the engine nor this fix can know.
        var resetMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName("System.Threading.Tasks.Task"),
                "ResetAsync")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
                        .WithType(SyntaxFactory.ParseTypeName("System.Threading.CancellationToken")))))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ParseStatement(
                        "throw new System.NotImplementedException(\"Reset the state this class shares "
                        + "between [Benchmark] methods, or replace IStateReset with [SharedState] if "
                        + "the carry-over is deliberate.\");")
                    .WithLeadingTrivia(
                        SyntaxFactory.Comment(
                            "// TODO: reset the state shared between [Benchmark] methods."),
                        SyntaxFactory.ElasticCarriageReturnLineFeed)));

        // Add the interface to the base list if not already present, plus a using directive.
        var baseTypes = typeDecl.BaseList?.Types ?? new SeparatedSyntaxList<BaseTypeSyntax>();

        var alreadyImplements = baseTypes.Any(bt =>
            bt.Type.ToString() is "IStateReset"
                or "NBenchmark.Lifecycle.IStateReset"
                or "global::NBenchmark.Lifecycle.IStateReset");

        var newTypeDecl = typeDecl
            .AddMembers(resetMethod);

        if (!alreadyImplements)
        {
            var baseType = SyntaxFactory.SimpleBaseType(
                SyntaxFactory.ParseTypeName(fullIStateResetName));

            newTypeDecl = newTypeDecl.WithBaseList(
                (newTypeDecl.BaseList ?? SyntaxFactory.BaseList())
                .AddTypes(baseType));
        }

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);

        // Ensure a using directive for NBenchmark.Lifecycle is present.
        var compilationRoot = (CompilationUnitSyntax)newRoot;

        var hasUsing = compilationRoot.Usings.Any(u =>
            u.Name?.ToString() is "NBenchmark.Lifecycle"
                or "global::NBenchmark.Lifecycle");

        if (!hasUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName("NBenchmark.Lifecycle"))
                .NormalizeWhitespace();

            compilationRoot = compilationRoot.AddUsings(usingDirective);
            newRoot = compilationRoot;
        }

        return document.WithSyntaxRoot(newRoot);
    }
}
