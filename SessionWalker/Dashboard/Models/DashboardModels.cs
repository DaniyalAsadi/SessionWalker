namespace SessionWalker.Dashboard.Models;

/// <summary>
/// Solution-level roll-up shown on the Overview page and Executive Summary
/// sheet. All values are derived from a single <see cref="AnalysisResult"/>
/// — building this model never re-runs Roslyn analysis.
/// </summary>
public sealed record SolutionSummaryModel(
    string SolutionName,
    string SolutionPath,
    DateTimeOffset AnalyzedAtUtc,
    int TotalProjects,
    int TotalDocuments,
    int TotalControllers,
    int TotalActions,
    int TotalActionsWithSession,
    int TotalSessionOperations,
    int TotalDiagnostics,
    int TotalCompilationErrors,
    int TotalWarnings,
    int TotalRejectedDetections,
    int ProjectsWithIssues,
    int ProjectsWithoutSessionUsage,
    int ProjectsNotAnalyzed);

/// <summary>
/// One row of the Project Analysis (health) table.
/// </summary>
public sealed record ProjectSummaryModel(
    string ProjectName,
    string AssemblyName,
    string ProjectPath,
    int Documents,
    int Controllers,
    int Actions,
    int SessionActions,
    int SessionOperations,
    int CompilationErrors,
    int Warnings,
    string Status,
    string Priority,
    string DeveloperAction,
    bool HasSessionUsage,
    bool HasRejectedDetections,
    bool IsAnalyzed,
    IReadOnlyList<ControllerDetailModel> ControllerDetails);

/// <summary>
/// Full inventory of one controller in a project: every action, whether or
/// not it touches Session. Lets the Project Detail page show action rows with
/// no findings instead of silently omitting them.
/// </summary>
public sealed record ControllerDetailModel(
    string Controller,
    string File,
    IReadOnlyList<ActionDetailModel> Actions);

/// <summary>
/// One action with its Session usage summary and the detected operations.
/// </summary>
public sealed record ActionDetailModel(
    string Action,
    string File,
    int Line,
    int Column,
    int SessionOperations,
    int Reads,
    int Writes,
    bool CanBeReadOnly,
    IReadOnlyList<SessionFindingModel> Operations);

/// <summary>
/// One detected Session operation, flattened for the Session Detection page
/// and the Session Findings Excel sheet.
/// </summary>
public sealed record SessionFindingModel(
    string Project,
    string Controller,
    string Action,
    string File,
    int Line,
    int Column,
    string Operation,
    string Mutation,
    string SessionKey,
    string SessionType,
    string DetectionMethod,
    string Confidence,
    string Expression,
    string AccessPath,
    string Note,
    string CanBeReadOnly);

/// <summary>
/// One problem surfaced by the run: compile error/warning, reference problem,
/// loader problem, analyzer failure or a rejected (not-detected) Session
/// candidate. Value, not text: impact and suggested fix are classified so
/// Excel conditional formatting and action generation can key off them.
/// </summary>
public sealed record DiagnosticItemModel(
    string Priority,
    string Project,
    string Category,
    string Message,
    string File,
    int Line,
    int Column,
    string Impact,
    string SuggestedFix,
    string Status);

/// <summary>
/// Why a developer should open the Investigation page.
/// </summary>
public enum InvestigationKind
{
    /// <summary>Project analyzed cleanly but nothing was detected (possible false negative).</summary>
    NoSessionDetection,

    /// <summary>Compilation/loading problems that limit or invalidate the analysis.</summary>
    CompilationProblem,

    /// <summary>A Session-looking expression was evaluated but rejected by the type mapping.</summary>
    RejectedDetection,

    /// <summary>An analyzer threw during the run.</summary>
    AnalyzerFailure
}

/// <summary>
/// An investigation case: what happened, why it might have happened, and what
/// the developer should do next.
/// </summary>
public sealed record InvestigationCaseModel(
    InvestigationKind Kind,
    string Title,
    string Project,
    string Component,
    string File,
    string Severity,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> PossibleReasons,
    string SuggestedAction,
    string SuggestedFix);

/// <summary>
/// An actionable, exportable work item. This is the unit consumed by the
/// "Developer To-Do List" worksheet — the workbook is a task list, not a
/// raw data dump.
/// </summary>
public sealed record TodoItemModel(
    string Id,
    string Priority,
    string Category,
    string Project,
    string Component,
    string Problem,
    string Evidence,
    string SuggestedSolution,
    string Owner,
    string Status,
    DateTimeOffset CreatedDate,
    DateTimeOffset? CompletedDate,
    string Notes);

/// <summary>
/// The complete dashboard/export view model. Built once from
/// <see cref="AnalysisResult"/> by <c>DashboardModelBuilder</c>, then consumed
/// by the HTML dashboard and the Excel export service. UI and export code
/// never touch Roslyn models directly.
/// </summary>
public sealed record AnalysisDashboardModel(
    SolutionSummaryModel Solution,
    IReadOnlyList<ProjectSummaryModel> Projects,
    IReadOnlyList<SessionFindingModel> SessionFindings,
    IReadOnlyList<DiagnosticItemModel> Diagnostics,
    IReadOnlyList<InvestigationCaseModel> Investigations,
    IReadOnlyList<TodoItemModel> TodoItems);
