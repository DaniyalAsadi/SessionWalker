using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;

namespace SessionWalker.Analyzer;

/// <summary>
/// Second-phase, interprocedural analysis described in the design brief
/// (sections 11/12): "does calling this method potentially mutate Session,
/// even though the call site itself never touches Session directly?".
///
/// Deliberately simple, bounded BFS rather than a general points-to
/// analysis:
///   - starts from an invoked method and inspects its body for direct
///     Session mutations (same type-based check SessionUsageAnalyzer uses
///     at the top level), which naturally covers both:
///       (11) SomeMethod(Session) where the parameter is typed as a Session
///            type and is mutated inside the method body, and
///       (12) SessionHelper.SetUserId(id) where the method has no Session
///            parameter at all but reaches into HttpContext.Current.Session
///            itself,
///     without needing two separate code paths — a parameter or a static
///     access both resolve to a provably-Session-typed expression inside
///     the callee's own semantic model.
///   - if the callee itself makes no direct Session access, follows calls
///     to further methods declared in the same compilation, up to
///     <see cref="MaxDepth"/> hops.
///   - only trusts methods whose source is available in the current
///     Compilation; methods from referenced binary-only assemblies are
///     simply not traceable and are not reported as writes.
///   - results found one hop away, outside any conditional branch, are
///     reported at High confidence per the design brief's own worked
///     example (SessionHelper.SetUserId). Results reached through a
///     conditional branch (if/switch/loop/catch) or more than one hop are
///     Medium confidence, since the call site can no longer be sure the
///     mutation is unconditionally reached.
///   - every distinct method is only ever walked once per analysis run: the
///     top-level result is cached by method symbol, so a helper called from
///     fifty different actions is only inspected once (see section 21,
///     performance).
/// </summary>
public sealed class HelperMethodTracer(
    Solution solution,
    ISemanticModelCache cache)
{
    private const int MaxDepth = 4;

    private readonly ConcurrentDictionary<IMethodSymbol, IReadOnlyList<TraceHit>> _resultCache =
        new(SymbolEqualityComparer.Default);

    public sealed record TraceHit(
        SessionMutationKind Kind,
        string Key,
        string ViaMethodDisplayName,
        int HopCount,
        bool ReachedThroughConditional,
        SyntaxNode MutationNode,
        SyntaxTree MutationTree)
    {
        public ConfidenceLevel Confidence => HopCount == 0 && !ReachedThroughConditional
            ? ConfidenceLevel.High
            : ConfidenceLevel.Medium;

        public SessionMutationKind Kind { get; } = Kind;
        public string Key { get; } = Key;
        public string ViaMethodDisplayName { get; } = ViaMethodDisplayName;
        public int HopCount { get; } = HopCount;
        public bool ReachedThroughConditional { get; } = ReachedThroughConditional;
        public SyntaxNode MutationNode { get; } = MutationNode;
        public SyntaxTree MutationTree { get; } = MutationTree;
    }

    /// <summary>
    /// Top-level entry point: what does calling <paramref name="method"/>
    /// potentially do to Session?
    /// </summary>
    public async Task<IReadOnlyList<TraceHit>> GetReachableSessionMutationsAsync(
        Compilation compilation,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        if (_resultCache.TryGetValue(method, out var cached))
        {
            return cached;
        }

        var visited = new HashSet<IMethodSymbol>(
            SymbolEqualityComparer.Default);

        var hits = new List<TraceHit>();

        await WalkAsync(
            compilation,
            method,
            visited,
            hits,
            hop: 0,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TraceHit> result = hits.Count == 0
            ? Array.Empty<TraceHit>()
            : hits.ToArray();

        _resultCache.TryAdd(method, result);

        return result;
    }

    private async Task WalkAsync(
        Compilation currentCompilation,
        IMethodSymbol method,
        HashSet<IMethodSymbol> visited,
        List<TraceHit> hits,
        int hop,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (hop >= MaxDepth || !visited.Add(method))
        {
            return;
        }

        var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();

        if (declRef is null)
        {
            return;
        }

        if (declRef.GetSyntax(cancellationToken) is not MethodDeclarationSyntax methodDecl)
        {
            return;
        }

        SyntaxNode? body = methodDecl.Body ?? (SyntaxNode?)methodDecl.ExpressionBody;

        if (body is null)
        {
            return;
        }

        var tree = methodDecl.SyntaxTree;

        var owningCompilation = await GetOwningCompilationAsync(
            currentCompilation,
            method,
            tree,
            cancellationToken).ConfigureAwait(false);

        if (owningCompilation is null)
        {
            return;
        }

        // Important:
        // Never ask a Compilation for a SemanticModel of a tree
        // which does not belong to that Compilation.
        if (!owningCompilation.SyntaxTrees.Contains(tree))
        {
            return;
        }

        var model = cache.GetSemanticModel(
            owningCompilation,
            tree);

        var displayName = $"{method.ContainingType?.Name}.{method.Name}";

        foreach (var node in body.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case AssignmentExpressionSyntax assignment
                    when assignment.Left is ElementAccessExpressionSyntax elementAccess
                    && SessionSymbolDetector.MatchesByType(model, elementAccess.Expression):

                    hits.Add(new TraceHit(
                        SessionMutationKind.Assignment,
                        TryGetStringLiteralKey(
                            elementAccess.ArgumentList.Arguments
                                .FirstOrDefault()?.Expression),
                        displayName,
                        hop,
                        IsInsideConditional(assignment, body),
                        assignment,
                        tree));

                    break;

                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    && SessionSymbolDetector.MutatingMethodNames.Contains(
                        memberAccess.Name.Identifier.ValueText)
                    && SessionSymbolDetector.MatchesByType(
                        model,
                        memberAccess.Expression):

                    hits.Add(new TraceHit(
                        MapMutatingMethodName(
                            memberAccess.Name.Identifier.ValueText),
                        TryGetStringLiteralKey(
                            invocation.ArgumentList.Arguments
                                .FirstOrDefault()?.Expression),
                        displayName,
                        hop,
                        IsInsideConditional(invocation, body),
                        invocation,
                        tree));

                    break;

                case InvocationExpressionSyntax nestedInvocation:

                    if (model.GetSymbolInfo(
                            nestedInvocation,
                            cancellationToken).Symbol is not IMethodSymbol nestedMethod)
                    {
                        break;
                    }

                    if (SymbolEqualityComparer.Default.Equals(
                            nestedMethod,
                            method))
                    {
                        break;
                    }

                    await WalkAsync(
                        owningCompilation,
                        nestedMethod,
                        visited,
                        hits,
                        hop + 1,
                        cancellationToken).ConfigureAwait(false);

                    break;
            }
        }
    }

    private async Task<Compilation?> GetOwningCompilationAsync(
        Compilation currentCompilation,
        IMethodSymbol method,
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        // Fast path:
        // Most calls remain inside the same project.
        if (currentCompilation.SyntaxTrees.Contains(syntaxTree))
        {
            return currentCompilation;
        }

        /*
         * Cross-project call:
         *
         * Project A Compilation
         *      |
         *      +--> method declared in Project B
         *
         * Find Project B from the method's containing assembly.
         */
        var targetProject = solution.GetProject(
            method.ContainingAssembly,
            cancellationToken);

        if (targetProject is null)
        {
            // Fallback: try resolving the project from the SyntaxTree.
            var document = solution.GetDocument(syntaxTree);

            if (document is null)
            {
                return null;
            }

            targetProject = document.Project;
        }

        var targetCompilation = await targetProject
            .GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);

        if (targetCompilation is null)
        {
            return null;
        }

        // Defensive check: prevents exactly the exception you are seeing.
        if (!targetCompilation.SyntaxTrees.Contains(syntaxTree))
        {
            return null;
        }

        return targetCompilation;
    }

    private static bool IsInsideConditional(
        SyntaxNode node,
        SyntaxNode methodBody)
    {
        for (var current = node.Parent;
             current is not null && current != methodBody.Parent;
             current = current.Parent)
        {
            if (current is IfStatementSyntax
                or SwitchStatementSyntax
                or SwitchExpressionSyntax
                or ConditionalExpressionSyntax
                or WhileStatementSyntax
                or ForStatementSyntax
                or ForEachStatementSyntax
                or DoStatementSyntax
                or CatchClauseSyntax)
            {
                return true;
            }
        }

        return false;
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

    private static string TryGetStringLiteralKey(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        return null;
    }
}