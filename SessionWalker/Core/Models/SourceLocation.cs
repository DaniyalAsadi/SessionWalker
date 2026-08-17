namespace SessionWalker.Core.Models;

/// <summary>
/// A resolved, file-system-relative source position. Line/Column are 1-based
/// for human-readable output (Roslyn's own LinePosition is 0-based).
/// </summary>
public sealed record SourceLocation(
    string FilePath,
    int Line,
    int Column)
{
    public override string ToString() => $"{FilePath}:{Line}:{Column}";
    public string FilePath { get; } = FilePath;
    public int Line { get; } = Line;
    public int Column { get; } = Column;
}
