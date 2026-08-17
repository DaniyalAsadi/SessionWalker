using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SessionWalker.Analyzer;

/// <summary>
/// Identifies ASP.NET MVC 5 controller classes and their action methods
/// using semantic symbol checks, never string matching on the class name.
/// A class named "FooController" that does not derive from
/// System.Web.Mvc.Controller (or System.Web.Mvc.ControllerBase) is not
/// treated as a controller.
/// </summary>
public static class ControllerActionDetector
{
    private const string MvcControllerBaseMetadataName = "System.Web.Mvc.ControllerBase";
    private const string NonActionAttributeMetadataName = "System.Web.Mvc.NonActionAttribute";
    private const string ChildActionOnlyAttributeMetadataName = "System.Web.Mvc.ChildActionOnlyAttribute";

    public sealed record ControllerCandidate(INamedTypeSymbol Symbol, ClassDeclarationSyntax Declaration, SyntaxTree Tree)
    {
        public INamedTypeSymbol Symbol { get; } = Symbol;
        public ClassDeclarationSyntax Declaration { get; } = Declaration;
        public SyntaxTree Tree { get; } = Tree;
    }

    public sealed record ActionCandidate(IMethodSymbol Symbol, MethodDeclarationSyntax Declaration, SyntaxTree Tree)
    {
        public IMethodSymbol Symbol { get; } = Symbol;
        public MethodDeclarationSyntax Declaration { get; } = Declaration;
        public SyntaxTree Tree { get; } = Tree;
    }

    /// <summary>
    /// Finds every class in the compilation that (transitively) derives from
    /// System.Web.Mvc.ControllerBase. Returns an empty sequence — never
    /// throws — if System.Web.Mvc is not referenced by the project at all,
    /// since not every project in a solution is necessarily an MVC web
    /// project.
    /// </summary>
    public static IEnumerable<ControllerCandidate> FindControllers(Compilation compilation, CancellationToken cancellationToken)
    {
        var controllerBase = compilation.GetTypeByMetadataName(MvcControllerBaseMetadataName);
        if (controllerBase is null)
        {
            yield break;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(classDecl, cancellationToken) is not INamedTypeSymbol classSymbol)
                {
                    continue;
                }

                if (IsControllerType(classSymbol, controllerBase))
                {
                    yield return new ControllerCandidate(classSymbol, classDecl, tree);
                }
            }
        }
    }

    /// <summary>
    /// Resolves the well-known System.Web.Mvc.ControllerBase symbol for a
    /// compilation, or null if the project does not reference System.Web.Mvc.
    /// Exposed so callers (e.g. SessionUsageAnalyzer, when mapping a random
    /// syntax node back to its enclosing controller/action) can reuse the
    /// same single-symbol check without re-enumerating the whole compilation.
    /// </summary>
    public static INamedTypeSymbol GetControllerBaseSymbol(Compilation compilation) =>
        compilation.GetTypeByMetadataName(MvcControllerBaseMetadataName);

    public static bool IsControllerType(INamedTypeSymbol classSymbol, INamedTypeSymbol controllerBase)
    {
        if (classSymbol is null || controllerBase is null)
        {
            return false;
        }

        return !classSymbol.IsAbstract && DerivesFrom(classSymbol, controllerBase);
    }

    public static bool IsActionMethod(IMethodSymbol methodSymbol)
    {
        if (methodSymbol is null)
        {
            return false;
        }

        if (methodSymbol.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        if (methodSymbol.IsStatic || methodSymbol.MethodKind != MethodKind.Ordinary)
        {
            return false;
        }

        return !HasAttribute(methodSymbol, NonActionAttributeMetadataName);
    }

    /// <summary>
    /// Finds MVC action methods on a controller: public, non-static,
    /// instance methods, not decorated with [NonAction], and not special
    /// members (constructors, property accessors). MVC treats any such
    /// method as an action regardless of return type, so — per the design
    /// brief — this deliberately does not filter by return type name; it
    /// only records the return type for display purposes.
    /// </summary>
    public static IEnumerable<ActionCandidate> FindActions(Compilation compilation, ControllerCandidate controller, CancellationToken cancellationToken)
    {
        var model = compilation.GetSemanticModel(controller.Tree);

        foreach (var methodDecl in controller.Declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (model.GetDeclaredSymbol(methodDecl, cancellationToken) is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            if (!IsActionMethod(methodSymbol))
            {
                continue;
            }

            yield return new ActionCandidate(methodSymbol, methodDecl, controller.Tree);
        }
    }

    public static bool IsChildActionOnly(IMethodSymbol method) =>
        HasAttribute(method, ChildActionOnlyAttributeMetadataName);

    private static bool HasAttribute(ISymbol symbol, string attributeMetadataName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass == null)
            {
                continue;
            }

            var name = attribute.AttributeClass.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);

            if (name.StartsWith("global::", StringComparison.Ordinal))
            {
                name = name.Substring("global::".Length);
            }

            if (string.Equals(
                    name,
                    attributeMetadataName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseCandidate)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseCandidate))
            {
                return true;
            }
        }

        return false;
    }
}
