namespace SessionWalker.Core.Models;

/// <summary>
/// A single, concrete Session access or mutation site discovered by
/// <c>SessionUsageAnalyzer</c> (or a future analyzer that reuses this model).
/// </summary>
public sealed record SessionOperationResult(
    SessionOperationType OperationType,
    SessionMutationKind? MutationKind,
    string Key,
    SourceLocation Location,
    string Expression,
    SymbolInfo Symbol,
    ConfidenceLevel Confidence,
    AccessPath AccessPath,
    string Note = null)
{
    public bool IsWrite =>
        OperationType is SessionOperationType.Write
            or SessionOperationType.Remove
            or SessionOperationType.Clear
            or SessionOperationType.Abandon;

    public bool IsRead => OperationType == SessionOperationType.Read;
    public SessionOperationType OperationType { get; } = OperationType;
    public SessionMutationKind? MutationKind { get; } = MutationKind;
    public string Key { get; } = Key;
    public SourceLocation Location { get; } = Location;
    public string Expression { get; } = Expression;
    public SymbolInfo Symbol { get; } = Symbol;
    public ConfidenceLevel Confidence { get; } = Confidence;
    public AccessPath AccessPath { get; } = AccessPath;
    public string Note { get; } = Note;
}
