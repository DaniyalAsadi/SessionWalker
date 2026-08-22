namespace SessionWalker.Core.Models;

/// <summary>
/// Why a Session-looking expression was not accepted as a Session operation.
/// This is observation/diagnostics only — the value never feeds back into the
/// detection decision, so the outcome of <c>SessionUsageAnalyzer</c> is
/// identical whether or not rejections are recorded.
/// </summary>
public enum SessionTypeRejectionReason
{
    /// <summary>Not a rejection — the expression matched the known Session type mapping.</summary>
    None,

    /// <summary>The expression's type could not be resolved at all (missing reference, error type, ...).</summary>
    TypeNotResolved,

    /// <summary>The expression resolved to a concrete type that is not in the known Session type mapping.</summary>
    UnsupportedSessionTypeMapping
}

/// <summary>
/// A syntactically Session-like expression (indexer access or mutating method
/// call) whose receiver type was not recognized by
/// <see cref="SessionWalker.Analyzer.SessionSymbolDetector"/>. Recorded purely
/// so the dashboard can tell developers *why* something was not detected and
/// what to do about it; it is never added to the detected operation set.
/// </summary>
public sealed record RejectedCandidate(
    string Expression,
    string ResolvedType,
    string ConvertedType,
    SessionTypeRejectionReason Reason,
    SourceLocation Location,
    SymbolInfo Symbol,
    string Note = null)
{
    public string Expression { get; } = Expression;
    public string ResolvedType { get; } = ResolvedType;
    public string ConvertedType { get; } = ConvertedType;
    public SessionTypeRejectionReason Reason { get; } = Reason;
    public SourceLocation Location { get; } = Location;
    public SymbolInfo Symbol { get; } = Symbol;
    public string Note { get; } = Note;
}
