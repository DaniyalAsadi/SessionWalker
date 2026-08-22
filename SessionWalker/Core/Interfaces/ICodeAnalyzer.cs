using Microsoft.CodeAnalysis;

namespace SessionWalker.Core.Interfaces;

/// <summary>
/// Everything a single analyzer needs to inspect one project. Analyzers are
/// intentionally scoped to a project (not the whole solution) so that
/// projects can be analyzed independently/in parallel; cross-project
/// interprocedural work is done by consulting <see cref="Solution"/> when a
/// symbol resolves outside the current project.
/// </summary>
public sealed class AnalysisContext
{
    public Solution Solution { get; set; }

    public Project Project { get; set; }

    public Compilation Compilation { get; set; }

    /// <summary>Shared per-run cache of SemanticModel instances keyed by SyntaxTree, to avoid recomputation across analyzers.</summary>
    public ISemanticModelCache SemanticModelCache { get; set; }

    public bool Verbose { get; set; }
}

/// <summary>
/// Caches <see cref="SemanticModel"/> instances per <see cref="SyntaxTree"/>
/// so that multiple analyzers (and multiple passes within one analyzer, e.g.
/// interprocedural tracing) never re-bind the same tree twice.
/// </summary>
public interface ISemanticModelCache
{
    SemanticModel GetSemanticModel(Compilation compilation, SyntaxTree tree);
}

/// <summary>
/// Output of a single analyzer's run against a single project. Analyzers
/// return their own strongly-typed payload plus free-text diagnostics;
/// <c>Payload</c> is deliberately <see cref="object"/> so the framework does
/// not need to know about every analyzer's result shape (see
/// <c>SessionAnalyzerResult</c> for the payload produced by
/// <c>SessionUsageAnalyzer</c>).
/// </summary>
public sealed record AnalyzerResult(
    string AnalyzerName,
    object Payload,
    IReadOnlyList<string> Diagnostics)
{
    public string AnalyzerName { get; } = AnalyzerName;
    public object Payload { get; } = Payload;
    public IReadOnlyList<string> Diagnostics { get; } = Diagnostics;
}

/// <summary>
/// The extensibility seam described in the design brief: every analyzer
/// (Session usage, ViewBag, TempData, HttpContext, ...) implements this and
/// is discovered/registered independently of the CLI or the orchestrator.
/// </summary>
public interface ICodeAnalyzer
{
    string Name { get; }

    string Description { get; }

    Task<AnalyzerResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken);
}
