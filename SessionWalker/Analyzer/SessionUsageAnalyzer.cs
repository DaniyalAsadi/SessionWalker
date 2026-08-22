using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;
using SymbolInfo = SessionWalker.Core.Models.SymbolInfo;

namespace SessionWalker.Analyzer;

/// <summary>
/// Finds every ASP.NET Session read and write in a project, using semantic
/// (not textual) analysis:
///
///   1. Direct access: <c>Session[...]</c>, <c>HttpContext.Session[...]</c>,
///      <c>HttpContext.Current.Session[...]</c> and their .Add/.Remove/
///      .RemoveAll/.Clear/.Abandon method-call equivalents.
///   2. Indirect access through a local variable/parameter/field whose type
///      the compiler has already resolved to one of the known ASP.NET
///      Session types (see <see cref="SessionSymbolDetector"/>) — this is
///      "free" once you ask the semantic model for a type, no separate
///      data-flow analysis is required, because the compiler already did
///      the type inference.
///   3. Interprocedural access: a call site that itself never touches
///      Session, but calls into a method (with source available in the
///      solution) that does — see <see cref="HelperMethodTracer"/>.
///
/// A node only ever becomes a Session operation when the semantic model can
/// prove the receiver's type is a known Session type; a variable merely
/// *named* "session" that resolves to some other type is never reported
/// (design brief section 5/10, test case 10/11).
/// </summary>
public sealed class SessionUsageAnalyzer : ICodeAnalyzer
{
    public string Name => "SessionUsageAnalyzer";

    public string Description =>
        "Detects direct, indirect and interprocedural reads/writes of ASP.NET Session state (System.Web.SessionState.*) using Roslyn semantic analysis.";

    public async Task<AnalyzerResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Compilation;
        var controllerBase = ControllerActionDetector.GetControllerBaseSymbol(compilation);
        var tracer = new HelperMethodTracer(context.Solution, context.SemanticModelCache);
        var diagnostics = new List<string>();

        var raw = new List<RawOperation>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsGeneratedOrExcluded(tree.FilePath))
            {
                continue;
            }

            SyntaxNode root;
            try
            {
                root = tree.GetRoot(cancellationToken);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Failed to read syntax tree '{tree.FilePath}': {ex.Message}");
                continue;
            }

            var model = context.SemanticModelCache.GetSemanticModel(compilation, tree);

            await AnalyzeTreeAsync(
                context,
                compilation,
                tree,
                root,
                model,
                controllerBase,
                tracer,
                raw,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
        }

        var controllers = ActionSessionSummaryBuilder.Build(compilation, context.Project.Name, controllerBase, raw, cancellationToken);

        var payload = new SessionAnalyzerResult(
            raw.Select(r => r.Result).ToList(),
            controllers);

        return new AnalyzerResult(Name, payload, diagnostics);
    }

    private async Task AnalyzeTreeAsync(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SyntaxNode root,
        SemanticModel model,
        INamedTypeSymbol controllerBase,
        HelperMethodTracer tracer,
        List<RawOperation> raw,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        // Nodes already reported as a direct mutating-method invocation are
        // excluded from the generic "interprocedural call" check below, so
        // we don't double count e.g. session.Add(...) as both a direct
        // write and a call into Add's (framework, unavailable) source.
        var alreadyHandledInvocations = new HashSet<InvocationExpressionSyntax>();

        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case ElementAccessExpressionSyntax elementAccess:
                    TryHandleElementAccess(context, compilation, tree, model, controllerBase, elementAccess, raw, cancellationToken);
                    break;

                case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    && SessionSymbolDetector.MutatingMethodNames.Contains(memberAccess.Name.Identifier.ValueText):
                    if (TryHandleMutatingInvocation(context, compilation, tree, model, controllerBase, invocation, memberAccess, raw, cancellationToken))
                    {
                        alreadyHandledInvocations.Add(invocation);
                    }
                    break;
            }
        }

        // Second pass: interprocedural. Kept separate from the switch above
        // so every invocation not already classified as a direct mutation
        // gets a chance to be traced into its target method's body.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (alreadyHandledInvocations.Contains(invocation))
            {
                continue;
            }

            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol invokedMethod)
            {
                continue;
            }

            // Only worth tracing into methods declared in source within this
            // solution; framework/BCL calls have no syntax to walk.
            if (invokedMethod.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            IReadOnlyList<HelperMethodTracer.TraceHit> hits;
            try
            {
                hits = await tracer.GetReachableSessionMutationsAsync(
                    compilation,
                    invokedMethod,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Interprocedural trace failed for '{invokedMethod.Name}': {ex.Message}");
                continue;
            }

            foreach (var hit in hits)
            {
                var symbolInfo = BuildSymbolInfo(context, compilation, tree, invocation, controllerBase, cancellationToken, out var containingMethod, out var containingClass);
                var location = ToSourceLocation(context.Project, tree, invocation.Span);

                var result = new SessionOperationResult(
                    OperationType: MapMutationKindToOperationType(hit.Kind),
                    MutationKind: hit.Kind,
                    Key: hit.Key,
                    Location: location,
                    Expression: Truncate(invocation.ToString()),
                    Symbol: symbolInfo,
                    Confidence: hit.Confidence,
                    AccessPath: AccessPath.InterproceduralCall,
                    Note: $"Indirect Session write via {hit.ViaMethodDisplayName} ({hit.HopCount + 1} hop(s)){(hit.ReachedThroughConditional ? ", reached through a conditional branch" : string.Empty)}");

                raw.Add(new RawOperation(result, containingMethod, containingClass));
            }
        }
    }

    private static void TryHandleElementAccess(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SemanticModel model,
        INamedTypeSymbol controllerBase,
        ElementAccessExpressionSyntax elementAccess,
        List<RawOperation> raw,
        CancellationToken cancellationToken)
    {
        if (!SessionSymbolDetector.MatchesByType(model, elementAccess.Expression))
        {
            return;
        }

        var isWrite = elementAccess.Parent is AssignmentExpressionSyntax assignment && assignment.Left == elementAccess;

        var symbolInfo = BuildSymbolInfo(context, compilation, tree, elementAccess, controllerBase, cancellationToken, out var containingMethod, out var containingClass);
        var location = ToSourceLocation(context.Project, tree, elementAccess.Span);

        var result = new SessionOperationResult(
            OperationType: isWrite ? SessionOperationType.Write : SessionOperationType.Read,
            MutationKind: isWrite ? SessionMutationKind.Assignment : null,
            Key: TryGetStringLiteralKey(elementAccess.ArgumentList.Arguments.FirstOrDefault()?.Expression),
            Location: location,
            Expression: Truncate(isWrite && elementAccess.Parent is not null ? elementAccess.Parent.ToString() : elementAccess.ToString()),
            Symbol: symbolInfo,
            Confidence: ConfidenceLevel.High,
            AccessPath: DetermineAccessPath(model, elementAccess.Expression));

        raw.Add(new RawOperation(result, containingMethod, containingClass));
    }

    private static bool TryHandleMutatingInvocation(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SemanticModel model,
        INamedTypeSymbol controllerBase,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        List<RawOperation> raw,
        CancellationToken cancellationToken)
    {
        if (!SessionSymbolDetector.MatchesByType(model, memberAccess.Expression))
        {
            return false;
        }

        var methodName = memberAccess.Name.Identifier.ValueText;
        var mutationKind = MapMutatingMethodName(methodName);

        var symbolInfo = BuildSymbolInfo(context, compilation, tree, invocation, controllerBase, cancellationToken, out var containingMethod, out var containingClass);
        var location = ToSourceLocation(context.Project, tree, invocation.Span);

        var result = new SessionOperationResult(
            OperationType: MapMutationKindToOperationType(mutationKind),
            MutationKind: mutationKind,
            Key: TryGetStringLiteralKey(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression),
            Location: location,
            Expression: Truncate(invocation.ToString()),
            Symbol: symbolInfo,
            Confidence: ConfidenceLevel.High,
            AccessPath: DetermineAccessPath(model, memberAccess.Expression));

        raw.Add(new RawOperation(result, containingMethod, containingClass));
        return true;
    }

    private static AccessPath DetermineAccessPath(SemanticModel model, ExpressionSyntax receiver)
    {
        // A direct access looks like Session / HttpContext.Session /
        // HttpContext.Current.Session syntactically — i.e. the receiver
        // resolves straight to a property/field, not to a local variable or
        // parameter symbol.
        var symbol = model.GetSymbolInfo(receiver).Symbol;
        return symbol switch
        {
            ILocalSymbol => AccessPath.LocalVariableIndirection,
            IParameterSymbol => AccessPath.MethodParameter,
            _ => AccessPath.Direct
        };
    }

    private static SymbolInfo BuildSymbolInfo(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SyntaxNode node,
        INamedTypeSymbol controllerBase,
        CancellationToken cancellationToken,
        out IMethodSymbol containingMethod,
        out INamedTypeSymbol containingClass)
    {
        var model = context.SemanticModelCache.GetSemanticModel(compilation, tree);

        var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        var classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();

        containingMethod = methodDecl is not null ? model.GetDeclaredSymbol(methodDecl, cancellationToken) : null;
        containingClass = classDecl is not null ? model.GetDeclaredSymbol(classDecl, cancellationToken) : null;

        var isController = ControllerActionDetector.IsControllerType(containingClass, controllerBase);
        var isAction = isController && ControllerActionDetector.IsActionMethod(containingMethod);

        return new SymbolInfo(
            SolutionPath: context.Solution.FilePath,
            ProjectName: context.Project.Name,
            AssemblyName: compilation.AssemblyName,
            Namespace: containingClass?.ContainingNamespace?.IsGlobalNamespace == false
                ? containingClass.ContainingNamespace.ToDisplayString()
                : null,
            ClassName: containingClass?.Name,
            MethodName: containingMethod?.Name,
            ControllerName: isController ? containingClass!.Name : null,
            ActionName: isAction ? containingMethod!.Name : null);
    }

    private static SourceLocation ToSourceLocation(Project project, SyntaxTree tree, TextSpan span)
    {
        var lineSpan = tree.GetLineSpan(span);
        var filePath = tree.FilePath;

        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (!string.IsNullOrEmpty(projectDir) && !string.IsNullOrEmpty(filePath))
        {
            try
            {
                filePath = GetRelativePath(projectDir, filePath);
            }
            catch (ArgumentException)
            {
                // Different volume/drive etc. — fall back to the absolute path.
            }
        }

        return new SourceLocation(
            filePath ?? "<unknown>",
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }

    private static bool IsGeneratedOrExcluded(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetStringLiteralKey(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        return null;
    }

    private static SessionMutationKind MapMutatingMethodName(string name) => name switch
    {
        "Add" => SessionMutationKind.Add,
        "Remove" => SessionMutationKind.Remove,
        "RemoveAt" => SessionMutationKind.Remove,
        "RemoveAll" => SessionMutationKind.RemoveAll,
        "Clear" => SessionMutationKind.Clear,
        "Abandon" => SessionMutationKind.Abandon,
        _ => SessionMutationKind.Add
    };

    private static SessionOperationType MapMutationKindToOperationType(SessionMutationKind kind) => kind switch
    {
        SessionMutationKind.Remove => SessionOperationType.Remove,
        SessionMutationKind.RemoveAll => SessionOperationType.Remove,
        SessionMutationKind.Clear => SessionOperationType.Clear,
        SessionMutationKind.Abandon => SessionOperationType.Abandon,
        _ => SessionOperationType.Write
    };

    private static string Truncate(string expression, int max = 200)
    {
        return expression.Length <= max
            ? expression
            : expression.Substring(0, max) + "...";
    }
    private static string GetRelativePath(string relativeTo, string path)
    {
        relativeTo = Path.GetFullPath(relativeTo);
        path = Path.GetFullPath(path);

        if (!relativeTo.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            relativeTo += Path.DirectorySeparatorChar;
        }

        var baseUri = new Uri(relativeTo);
        var pathUri = new Uri(path);

        if (!string.Equals(baseUri.Scheme, pathUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var relativeUri = baseUri.MakeRelativeUri(pathUri);

        return Uri.UnescapeDataString(relativeUri.ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }
    public sealed record RawOperation(SessionOperationResult Result, IMethodSymbol ContainingMethod, INamedTypeSymbol ContainingClass)
    {
        public SessionOperationResult Result { get; } = Result;
        public IMethodSymbol ContainingMethod { get; } = ContainingMethod;
        public INamedTypeSymbol ContainingClass { get; } = ContainingClass;
    }
}
