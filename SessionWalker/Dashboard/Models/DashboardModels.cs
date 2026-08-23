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
    int ProjectsNotAnalyzed)
{
    public string SolutionName { get; } = SolutionName;
    public string SolutionPath { get; } = SolutionPath;
    public DateTimeOffset AnalyzedAtUtc { get; } = AnalyzedAtUtc;
    public int TotalProjects { get; } = TotalProjects;
    public int TotalDocuments { get; } = TotalDocuments;
    public int TotalControllers { get; } = TotalControllers;
    public int TotalActions { get; } = TotalActions;
    public int TotalActionsWithSession { get; } = TotalActionsWithSession;
    public int TotalSessionOperations { get; } = TotalSessionOperations;
    public int TotalDiagnostics { get; } = TotalDiagnostics;
    public int TotalCompilationErrors { get; } = TotalCompilationErrors;
    public int TotalWarnings { get; } = TotalWarnings;
    public int TotalRejectedDetections { get; } = TotalRejectedDetections;
    public int ProjectsWithIssues { get; } = ProjectsWithIssues;
    public int ProjectsWithoutSessionUsage { get; } = ProjectsWithoutSessionUsage;
    public int ProjectsNotAnalyzed { get; } = ProjectsNotAnalyzed;
}

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
    IReadOnlyList<ControllerDetailModel> ControllerDetails)
{
    public string ProjectName { get; } = ProjectName;
    public string AssemblyName { get; } = AssemblyName;
    public string ProjectPath { get; } = ProjectPath;
    public int Documents { get; } = Documents;
    public int Controllers { get; } = Controllers;
    public int Actions { get; } = Actions;
    public int SessionActions { get; } = SessionActions;
    public int SessionOperations { get; } = SessionOperations;
    public int CompilationErrors { get; } = CompilationErrors;
    public int Warnings { get; } = Warnings;
    public string Status { get; } = Status;
    public string Priority { get; } = Priority;
    public string DeveloperAction { get; } = DeveloperAction;
    public bool HasSessionUsage { get; } = HasSessionUsage;
    public bool HasRejectedDetections { get; } = HasRejectedDetections;
    public bool IsAnalyzed { get; } = IsAnalyzed;
    public IReadOnlyList<ControllerDetailModel> ControllerDetails { get; } = ControllerDetails;
}

/// <summary>
/// Full inventory of one controller in a project: every action, whether or
/// not it touches Session. Lets the Project Detail page show action rows with
/// no findings instead of silently omitting them.
/// </summary>
public sealed record ControllerDetailModel(
    string Controller,
    string File,
    IReadOnlyList<ActionDetailModel> Actions)
{
    public string Controller { get; } = Controller;
    public string File { get; } = File;
    public IReadOnlyList<ActionDetailModel> Actions { get; } = Actions;
}

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
    IReadOnlyList<SessionFindingModel> Operations)
{
    public string Action { get; } = Action;
    public string File { get; } = File;
    public int Line { get; } = Line;
    public int Column { get; } = Column;
    public int SessionOperations { get; } = SessionOperations;
    public int Reads { get; } = Reads;
    public int Writes { get; } = Writes;
    public bool CanBeReadOnly { get; } = CanBeReadOnly;
    public IReadOnlyList<SessionFindingModel> Operations { get; } = Operations;
}

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
    string CanBeReadOnly)
{
    public string Project { get; } = Project;
    public string Controller { get; } = Controller;
    public string Action { get; } = Action;
    public string File { get; } = File;
    public int Line { get; } = Line;
    public int Column { get; } = Column;
    public string Operation { get; } = Operation;
    public string Mutation { get; } = Mutation;
    public string SessionKey { get; } = SessionKey;
    public string SessionType { get; } = SessionType;
    public string DetectionMethod { get; } = DetectionMethod;
    public string Confidence { get; } = Confidence;
    public string Expression { get; } = Expression;
    public string AccessPath { get; } = AccessPath;
    public string Note { get; } = Note;
    public string CanBeReadOnly { get; } = CanBeReadOnly;
}

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
    string Status)
{
    public string Priority { get; } = Priority;
    public string Project { get; } = Project;
    public string Category { get; } = Category;
    public string Message { get; } = Message;
    public string File { get; } = File;
    public int Line { get; } = Line;
    public int Column { get; } = Column;
    public string Impact { get; } = Impact;
    public string SuggestedFix { get; } = SuggestedFix;
    public string Status { get; } = Status;
}

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
    string SuggestedFix)
{
    public InvestigationKind Kind { get; } = Kind;
    public string Title { get; } = Title;
    public string Project { get; } = Project;
    public string Component { get; } = Component;
    public string File { get; } = File;
    public string Severity { get; } = Severity;
    public IReadOnlyList<string> Evidence { get; } = Evidence;
    public IReadOnlyList<string> PossibleReasons { get; } = PossibleReasons;
    public string SuggestedAction { get; } = SuggestedAction;
    public string SuggestedFix { get; } = SuggestedFix;
}

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
    string Notes)
{
    public string Id { get; } = Id;
    public string Priority { get; } = Priority;
    public string Category { get; } = Category;
    public string Project { get; } = Project;
    public string Component { get; } = Component;
    public string Problem { get; } = Problem;
    public string Evidence { get; } = Evidence;
    public string SuggestedSolution { get; } = SuggestedSolution;
    public string Owner { get; } = Owner;
    public string Status { get; } = Status;
    public DateTimeOffset CreatedDate { get; } = CreatedDate;
    public DateTimeOffset? CompletedDate { get; } = CompletedDate;
    public string Notes { get; } = Notes;
}

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
    IReadOnlyList<TodoItemModel> TodoItems)
{
    public SolutionSummaryModel Solution { get; } = Solution;
    public IReadOnlyList<ProjectSummaryModel> Projects { get; } = Projects;
    public IReadOnlyList<SessionFindingModel> SessionFindings { get; } = SessionFindings;
    public IReadOnlyList<DiagnosticItemModel> Diagnostics { get; } = Diagnostics;
    public IReadOnlyList<InvestigationCaseModel> Investigations { get; } = Investigations;
    public IReadOnlyList<TodoItemModel> TodoItems { get; } = TodoItems;
}
