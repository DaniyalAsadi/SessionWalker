using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SessionWalker.Core.Models;

namespace SessionWalker.Analyzer;

/// <summary>
/// Turns the flat list of <see cref="SessionUsageAnalyzer.RawOperation"/>
/// discovered across a project into the <see cref="ActionAnalysisResult"/> /
/// <see cref="ControllerAnalysisResult"/> shape the CLI and report writers
/// consume. This is where "for every controller action, calculate a
/// SessionUsageSummary" (design brief section 8/9) actually happens.
/// </summary>
public static class ActionSessionSummaryBuilder
{
    public static IReadOnlyList<ControllerAnalysisResult> Build(
        Compilation compilation,
        string projectName,
        INamedTypeSymbol controllerBase,
        IReadOnlyList<SessionUsageAnalyzer.RawOperation> rawOperations,
        CancellationToken cancellationToken)
    {
        var controllers = ControllerActionDetector.FindControllers(compilation, cancellationToken).ToList();
        if (controllers.Count == 0)
        {
            return [];
        }

        // Group operations by their containing method symbol once, so each
        // action's lookup below is O(1) instead of re-scanning every
        // operation per action.
        var opsByMethod = new Dictionary<IMethodSymbol, List<SessionOperationResult>>(SymbolEqualityComparer.Default);
        foreach (var raw in rawOperations)
        {
            if (raw.ContainingMethod is null)
            {
                continue;
            }

            if (!opsByMethod.TryGetValue(raw.ContainingMethod, out var list))
            {
                list = new List<SessionOperationResult>();
                opsByMethod[raw.ContainingMethod] = list;
            }

            list.Add(raw.Result);
        }

        var results = new List<ControllerAnalysisResult>();

        foreach (var controller in controllers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var actions = new List<ActionAnalysisResult>();

            foreach (var action in ControllerActionDetector.FindActions(compilation, controller, cancellationToken))
            {
                var operations = opsByMethod.TryGetValue(action.Symbol, out var list)
                    ? list
                    : new List<SessionOperationResult>();

                var location = ToSourceLocation(action.Declaration, action.Tree);

                actions.Add(new ActionAnalysisResult(
                    ProjectName: projectName,
                    Namespace: controller.Symbol.ContainingNamespace?.IsGlobalNamespace == false
                        ? controller.Symbol.ContainingNamespace.ToDisplayString()
                        : string.Empty,
                    ControllerName: controller.Symbol.Name,
                    ActionName: action.Symbol.Name,
                    Location: location,
                    Operations: operations));
            }

            results.Add(new ControllerAnalysisResult(
                ProjectName: projectName,
                Namespace: controller.Symbol.ContainingNamespace?.IsGlobalNamespace == false
                    ? controller.Symbol.ContainingNamespace.ToDisplayString()
                    : string.Empty,
                ControllerName: controller.Symbol.Name,
                FilePath: controller.Tree.FilePath,
                Actions: actions));
        }

        return results;
    }

    private static SourceLocation ToSourceLocation(MethodDeclarationSyntax declaration, SyntaxTree tree)
    {
        var lineSpan = tree.GetLineSpan(declaration.Identifier.Span);
        return new SourceLocation(
            tree.FilePath ?? "<unknown>",
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }
}
