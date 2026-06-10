using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.CodeFixes.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp)]
[Shared]
public sealed class RemoveStaticKeywordCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [DiagnosticIds.StaticBenchmarkMethod];

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null)
            return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var methodDecl = root.FindToken(diagnosticSpan.Start).Parent?.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (methodDecl is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove static modifier",
                cancellationToken => RemoveStaticAsync(context.Document, methodDecl, cancellationToken),
                "RemoveStatic"),
            diagnostic);
    }

    private static async Task<Document> RemoveStaticAsync(
        Document document,
        MethodDeclarationSyntax methodDecl,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (root is null)
            return document;

        var staticToken = methodDecl.Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.StaticKeyword));

        if (staticToken.IsKind(SyntaxKind.None))
            return document;

        var newModifiers = methodDecl.Modifiers.Remove(staticToken);
        var newMethod = methodDecl.WithModifiers(newModifiers);
        var newRoot = root.ReplaceNode(methodDecl, newMethod);
        return document.WithSyntaxRoot(newRoot);
    }
}
