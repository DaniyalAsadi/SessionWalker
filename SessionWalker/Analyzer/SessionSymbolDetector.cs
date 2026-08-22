using Microsoft.CodeAnalysis;

namespace SessionWalker.Analyzer;

/// <summary>
/// Central place that answers "is this type an ASP.NET Session object?".
/// Every other component (SessionUsageAnalyzer, HelperMethodTracer) must go
/// through this class rather than re-implementing name checks, so the
/// definition of "is a Session type" only ever lives in one place.
///
/// Deliberately conservative: a type only counts as a Session type if its
/// fully-qualified metadata name matches one of the known ASP.NET Session
/// types, or it implements/derives from one of them. A local variable or
/// field simply *named* "session" is never enough — see
/// <c>MatchesByType(ITypeSymbol?)</c>.
/// </summary>
public static class SessionSymbolDetector
{
    /// <summary>
    /// Fully-qualified metadata names of types that represent ASP.NET Session
    /// state. HttpSessionState is the concrete runtime type used by classic
    /// Web Forms/System.Web; HttpSessionStateBase/-Wrapper are the
    /// testable abstraction MVC controllers normally see via
    /// ControllerContext.HttpContext.Session.
    /// </summary>
    private static readonly HashSet<string> _knownSessionTypeNames = new(StringComparer.Ordinal)
    {
        "System.Web.SessionState.HttpSessionState",
        "System.Web.SessionState.HttpSessionStateBase",
        "System.Web.SessionState.HttpSessionStateWrapper",
        "System.Web.SessionState.IHttpSessionState",
    };

    /// <summary>
    /// Member names on a Session-typed receiver that constitute mutation
    /// (as opposed to <c>Session["x"]</c> indexer read/write, handled
    /// separately by the caller).
    /// </summary>
    public static readonly HashSet<string> MutatingMethodNames = new(StringComparer.Ordinal)
    {
        "Add", "Remove", "RemoveAll", "RemoveAt", "Clear", "Abandon"
    };

    /// <summary>
    /// True if the given type is, implements, or derives from one of the
    /// known ASP.NET Session types. Walks the base-type chain and all
    /// interfaces so that HttpSessionStateWrapper (which derives from
    /// HttpSessionStateBase) and any custom subclass are both recognized.
    /// </summary>
    public static bool MatchesByType(ITypeSymbol type)
    {
        if (type is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (IsKnownName(current))
            {
                return true;
            }
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (IsKnownName(iface))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownName(ITypeSymbol type)
    {
        var displayName = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (displayName.StartsWith("global::", StringComparison.Ordinal))
        {
            displayName = displayName.Substring("global::".Length);
        }

        return _knownSessionTypeNames.Contains(displayName);
    }

    /// <summary>
    /// Convenience overload: resolves the type of an expression via the
    /// semantic model and checks it. Returns false (never throws) if the
    /// expression's type cannot be determined — callers should treat that as
    /// "not provably Session" rather than guessing.
    /// </summary>
    public static bool MatchesByType(SemanticModel semanticModel, Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression)
    {
        var typeInfo = semanticModel.GetTypeInfo(expression);
        return MatchesByType(typeInfo.Type) || MatchesByType(typeInfo.ConvertedType);
    }
}
