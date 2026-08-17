using System.Collections.Generic;
using SessionWalker.Core.Models;

namespace SessionWalker.Analyzer;

/// <summary>
/// Strongly-typed payload of <c>SessionUsageAnalyzer</c>'s
/// <c>AnalyzerResult.Payload</c>. The orchestrator unpacks this per project
/// and folds it into the final <see cref="ProjectAnalysisResult"/>.
/// </summary>
public sealed record SessionAnalyzerResult(
    IReadOnlyList<SessionOperationResult> AllOperations,
    IReadOnlyList<ControllerAnalysisResult> Controllers)
{
    public IReadOnlyList<SessionOperationResult> AllOperations { get; } = AllOperations;
    public IReadOnlyList<ControllerAnalysisResult> Controllers { get; } = Controllers;
}
