using System;
using System.Collections.Generic;
using System.Linq;

namespace SessionWalker.Core.Models;

/// <summary>
/// Session-usage summary for a single MVC action method. This is the model
/// that answers the "can this action safely use SessionStateBehavior.ReadOnly"
/// question the tool exists to answer.
/// </summary>
public sealed record ActionAnalysisResult(
    string ProjectName,
    string Namespace,
    string ControllerName,
    string ActionName,
    SourceLocation Location,
    IReadOnlyList<SessionOperationResult> Operations)
{
    public bool UsesSession => Operations.Count > 0;

    public bool HasSessionRead => Operations.Any(o => o.IsRead);

    public bool HasSessionWrite => Operations.Any(o => o.IsWrite);

    /// <summary>
    /// True when there is at least one write/remove/clear/abandon operation,
    /// i.e. any mutation, as opposed to a plain assignment write.
    /// </summary>
    public bool HasSessionMutation => Operations.Any(o =>
        o.OperationType is SessionOperationType.Write
            or SessionOperationType.Remove
            or SessionOperationType.Clear
            or SessionOperationType.Abandon);

    /// <summary>
    /// True only when the action touches Session exclusively via reads, and
    /// has zero writes of any confidence. A single Low-confidence write is
    /// still enough to say "no" here, because we never want to recommend
    /// ReadOnly on a guess.
    /// </summary>
    public bool CanPotentiallyUseReadOnlySession =>
        !HasSessionWrite;

    public int SessionWriteCount => Operations.Count(o => o.IsWrite);

    public int SessionReadCount => Operations.Count(o => o.IsRead);
    public string ProjectName { get; set; } = ProjectName;
    public string Namespace { get; set; } = Namespace;
    public string ControllerName { get; set; } = ControllerName;
    public string ActionName { get; set; } = ActionName;
    public SourceLocation Location { get; set; } = Location;
    public IReadOnlyList<SessionOperationResult> Operations { get; set; } = Operations;
}

/// <summary>
/// Aggregation of all actions belonging to a single MVC controller class.
/// </summary>
public sealed record ControllerAnalysisResult(
    string ProjectName,
    string Namespace,
    string ControllerName,
    string FilePath,
    IReadOnlyList<ActionAnalysisResult> Actions)
{
    public bool AnyActionUsesSession => Actions.Any(a => a.UsesSession);

    public bool AllActionsCanBeReadOnly => Actions.Count > 0 && Actions.All(a => a.CanPotentiallyUseReadOnlySession);
    public string ProjectName { get; set; } = ProjectName;
    public string Namespace { get; set; } = Namespace;
    public string ControllerName { get; set; } = ControllerName;
    public string FilePath { get; set; } = FilePath;
    public IReadOnlyList<ActionAnalysisResult> Actions { get; set; } = Actions;
}

/// <summary>
/// Aggregation of all controllers/operations discovered within one C# project
/// (one .csproj) inside the analyzed solution.
/// </summary>
public sealed record ProjectAnalysisResult(
    string ProjectName,
    string AssemblyName,
    string ProjectFilePath,
    IReadOnlyList<ControllerAnalysisResult> Controllers,
    IReadOnlyList<SessionOperationResult> AllSessionOperations,
    IReadOnlyList<string> Diagnostics)
{
    public string ProjectName { get; set; } = ProjectName;
    public string AssemblyName { get; set; } = AssemblyName;
    public string ProjectFilePath { get; set; } = ProjectFilePath;
    public IReadOnlyList<ControllerAnalysisResult> Controllers { get; set; } = Controllers;
    public IReadOnlyList<SessionOperationResult> AllSessionOperations { get; set; } = AllSessionOperations;
    public IReadOnlyList<string> Diagnostics { get; set; } = Diagnostics;
}

/// <summary>
/// Root result for a full solution analysis run.
/// </summary>
public sealed record AnalysisResult(
    string SolutionPath,
    DateTimeOffset AnalyzedAtUtc,
    IReadOnlyList<ProjectAnalysisResult> Projects)
{
    public IEnumerable<ControllerAnalysisResult> AllControllers => Projects.SelectMany(p => p.Controllers);

    public IEnumerable<ActionAnalysisResult> AllActions => AllControllers.SelectMany(c => c.Actions);

    public IEnumerable<SessionOperationResult> AllSessionOperations => Projects.SelectMany(p => p.AllSessionOperations);
    public string SolutionPath { get; set; } = SolutionPath;
    public DateTimeOffset AnalyzedAtUtc { get; set; } = AnalyzedAtUtc;
    public IReadOnlyList<ProjectAnalysisResult> Projects { get; set; } = Projects;
}
