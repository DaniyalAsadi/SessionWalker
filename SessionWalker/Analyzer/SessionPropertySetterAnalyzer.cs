using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;
using SymbolInfo = SessionWalker.Core.Models.SymbolInfo;

namespace SessionWalker.Analyzer;

/// <summary>
/// Detects indirect ASP.NET Session writes that happen through property
/// setter assignments. When a property's setter body mutates Session state
/// (e.g. <c>Session["UserId"] = value</c>) and some call site assigns to
/// that property (e.g. <c>userContext.UserId = "123"</c>), the assignment
/// is reported as an indirect Session write with
/// <see cref="AccessPath.PropertySetter"/>.
///
/// Complements <see cref="SessionUsageAnalyzer"/> — it never re-reports a
/// direct Session access (those are already found by the primary analyzer).
/// Detection is purely semantic: the setter's Session receiver must resolve
/// to a known ASP.NET Session type via <see cref="SessionSymbolDetector"/>,
/// and the call-site property must resolve to a property symbol registered
/// in Phase 1. Name-based heuristics are never used for detection.
///
/// Scope (v1): source-declared properties with a visible setter body that
/// performs a direct Session write. Reflection, polymorphic dispatch, base
/// class resolution and nested helper calls are intentionally out of scope.
/// </summary>
public sealed class SessionPropertySetterAnalyzer : ICodeAnalyzer
{
    public string Name => "SessionPropertySetterAnalyzer";

    public string Description =>
        "Detects indirect ASP.NET Session writes caused by assignments to properties whose setters mutate Session state.";

    public Task<AnalyzerResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Compilation;
        var diagnostics = new List<string>();

        // Phase 1: collect all source properties whose setter body mutates
        // Session. Keyed by the property symbol so Phase 2 can do O(1)
        // lookups for every assignment expression.
        var sessionMutatingProperties = new Dictionary<IPropertySymbol, PropertySessionMutationInfo>(
            SymbolEqualityComparer.Default);

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

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryRegisterSessionMutatingProperty(context, compilation, tree, model, property, sessionMutatingProperties, cancellationToken);
            }
        }

        // Phase 2: find every assignment to one of the registered
        // properties and emit a SessionOperationResult for each.
        var operations = new List<SessionOperationResult>();
        var controllerBase = ControllerActionDetector.GetControllerBaseSymbol(compilation);

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

            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Only simple assignment (=) constitutes "set the property".
                // Compound assignments (+=, etc.) don't go through the setter
                // in a way that maps to a single Session write.
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    continue;
                }

                TryHandlePropertyAssignment(
                    context, compilation, tree, model, controllerBase,
                    assignment, sessionMutatingProperties, operations, cancellationToken);
            }
        }

        var payload = new SessionAnalyzerResult(
            operations,
            Array.Empty<ControllerAnalysisResult>(),
            Array.Empty<RejectedCandidate>());

        return Task.FromResult(new AnalyzerResult(Name, payload, diagnostics));
    }

    // ------------------------------------------------------------------
    // Phase 1: register Session-mutating properties
    // ------------------------------------------------------------------

    private static void TryRegisterSessionMutatingProperty(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SemanticModel model,
        PropertyDeclarationSyntax property,
        Dictionary<IPropertySymbol, PropertySessionMutationInfo> registry,
        CancellationToken cancellationToken)
    {
        if (model.GetDeclaredSymbol(property, cancellationToken) is not IPropertySymbol propertySymbol)
        {
            return;
        }

        if (propertySymbol.SetMethod is null)
        {
            return;
        }

        // We need a setter body to inspect. Expression-bodied properties
        // (get => ...) have no setter block; auto-properties have no body.
        var setterAccessor = property.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        SyntaxNode? setterBody = setterAccessor?.Body ?? (SyntaxNode?)setterAccessor?.ExpressionBody;

        if (setterBody is null && property.ExpressionBody is null)
        {
            return;
        }

        // An expression-bodied property (public string X => ...) is a getter
        // only — never a setter. Skip it.
        if (setterBody is null)
        {
            return;
        }

        // Look for the first direct Session write inside the setter.
        var mutation = FindFirstSessionMutationInSetter(model, setterBody, cancellationToken);
        if (mutation is null)
        {
            return;
        }

        var setterLocation = ToSourceLocation(context.Project, tree, setterBody.Span);

        registry[propertySymbol] = new PropertySessionMutationInfo(
            PropertySymbol: propertySymbol,
            SetterLocation: setterLocation,
            SessionOperation: mutation.Value.Operation,
            SessionMutationKind: mutation.Value.MutationKind,
            SessionKey: mutation.Value.Key,
            ContainingTypeName: propertySymbol.ContainingType?.Name);
    }

    /// <summary>
    /// Walks a setter body looking for the first direct Session mutation
    /// (indexer assignment or mutating method call). Returns null when the
    /// setter does not touch Session.
    /// </summary>
    private static SetterSessionMutation? FindFirstSessionMutationInSetter(
        SemanticModel model,
        SyntaxNode setterBody,
        CancellationToken cancellationToken)
    {
        foreach (var node in setterBody.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case ElementAccessExpressionSyntax elementAccess:
                {
                    if (!SessionSymbolDetector.MatchesByType(model, elementAccess.Expression))
                    {
                        break;
                    }

                    var isWrite = elementAccess.Parent is AssignmentExpressionSyntax parentAssignment
                        && parentAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && parentAssignment.Left == elementAccess;

                    if (!isWrite)
                    {
                        break;
                    }

                    return new SetterSessionMutation(
                        SessionOperationType.Write,
                        SessionMutationKind.Assignment,
                        TryGetStringLiteralKey(elementAccess.ArgumentList.Arguments.FirstOrDefault()?.Expression));
                }

                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax memberAccess
                        && SessionSymbolDetector.MutatingMethodNames.Contains(memberAccess.Name.Identifier.ValueText):
                {
                    if (!SessionSymbolDetector.MatchesByType(model, memberAccess.Expression))
                    {
                        break;
                    }

                    var methodName = memberAccess.Name.Identifier.ValueText;
                    var mutationKind = MapMutatingMethodName(methodName);

                    return new SetterSessionMutation(
                        MapMutationKindToOperationType(mutationKind),
                        mutationKind,
                        TryGetStringLiteralKey(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression));
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Phase 2: detect assignments to registered properties
    // ------------------------------------------------------------------

    private static void TryHandlePropertyAssignment(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SemanticModel model,
        INamedTypeSymbol controllerBase,
        AssignmentExpressionSyntax assignment,
        Dictionary<IPropertySymbol, PropertySessionMutationInfo> registry,
        List<SessionOperationResult> operations,
        CancellationToken cancellationToken)
    {
        var symbol = model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
        if (symbol is not IPropertySymbol assignedProperty)
        {
            return;
        }

        if (!registry.TryGetValue(assignedProperty, out var mutationInfo))
        {
            return;
        }

        var symbolInfo = BuildSymbolInfo(context, compilation, tree, model, assignment, controllerBase, cancellationToken);
        var location = ToSourceLocation(context.Project, tree, assignment.Span);

        var result = new SessionOperationResult(
            OperationType: mutationInfo.SessionOperation,
            MutationKind: mutationInfo.SessionMutationKind,
            Key: mutationInfo.SessionKey,
            Location: location,
            Expression: Truncate(assignment.ToString()),
            Symbol: symbolInfo,
            Confidence: ConfidenceLevel.High,
            AccessPath: AccessPath.PropertySetter,
            Note: $"Indirect Session write through property setter '{mutationInfo.ContainingTypeName}.{assignedProperty.Name}' " +
                  $"(setter at {mutationInfo.SetterLocation})");

        operations.Add(result);
    }

    // ------------------------------------------------------------------
    // Helpers (intentionally parallel to SessionUsageAnalyzer's helpers so
    // output shapes stay consistent; duplicated rather than shared to keep
    // the two analyzers independently evolvable per the design brief).
    // ------------------------------------------------------------------

    private static SymbolInfo BuildSymbolInfo(
        AnalysisContext context,
        Compilation compilation,
        SyntaxTree tree,
        SemanticModel model,
        SyntaxNode node,
        INamedTypeSymbol controllerBase,
        CancellationToken cancellationToken)
    {
        var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        var classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();

        var containingMethod = methodDecl is not null ? model.GetDeclaredSymbol(methodDecl, cancellationToken) : null;
        var containingClass = classDecl is not null ? model.GetDeclaredSymbol(classDecl, cancellationToken) : null;

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

    private static string? TryGetStringLiteralKey(ExpressionSyntax? expression)
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

    // ------------------------------------------------------------------
    // Internal models
    // ------------------------------------------------------------------

    private sealed record PropertySessionMutationInfo(
        IPropertySymbol PropertySymbol,
        SourceLocation SetterLocation,
        SessionOperationType SessionOperation,
        SessionMutationKind? SessionMutationKind,
        string? SessionKey,
        string? ContainingTypeName)
    {
        public IPropertySymbol PropertySymbol { get; } = PropertySymbol;
        public SourceLocation SetterLocation { get; } = SetterLocation;
        public SessionOperationType SessionOperation { get; } = SessionOperation;
        public SessionMutationKind? SessionMutationKind { get; } = SessionMutationKind;
        public string? SessionKey { get; } = SessionKey;
        public string? ContainingTypeName { get; } = ContainingTypeName;
    }

    private readonly struct SetterSessionMutation
    {
        public SessionOperationType Operation { get; }
        public SessionMutationKind MutationKind { get; }
        public string? Key { get; }

        public SetterSessionMutation(SessionOperationType operation, SessionMutationKind mutationKind, string? key)
        {
            Operation = operation;
            MutationKind = mutationKind;
            Key = key;
        }
    }
}
