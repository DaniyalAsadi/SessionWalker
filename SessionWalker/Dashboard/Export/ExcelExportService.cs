using SessionWalker.Dashboard.Models;

namespace SessionWalker.Dashboard.Export;

/// <summary>
/// Generates the developer to-do workbook (.xlsx) from
/// <see cref="AnalysisDashboardModel"/>.
///
/// The workbook is deliberately structured as a task list, not a raw data
/// dump:
///   1. Executive Summary  — does the run look healthy?
///   2. Project Analysis   — per-project health + developer action
///   3. Session Findings   — every detected Session operation
///   4. Diagnostics        — compile/reference/analyzer problems, classified
///   5. Developer To-Do List — the actionable work items
///
/// This service consumes the dashboard view model only; it never touches
/// Roslyn, never re-runs analysis and contains no UI logic.
/// </summary>
public sealed class ExcelExportService
{
    public Task ExportAsync(AnalysisDashboardModel model, string destinationPath, CancellationToken cancellationToken)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("An output path is required.", nameof(destinationPath));
        }

        using var workbook = new XlsxWorkbook();

        WriteExecutiveSummary(workbook, model.Solution);
        WriteProjectAnalysis(workbook, model.Projects);
        WriteSessionFindings(workbook, model.SessionFindings);
        WriteDiagnostics(workbook, model.Diagnostics);
        WriteTodoList(workbook, model.TodoItems);

        workbook.Save(destinationPath, cancellationToken);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Sheet 1: Executive Summary
    // ------------------------------------------------------------------

    private static void WriteExecutiveSummary(XlsxWorkbook workbook, SolutionSummaryModel solution)
    {
        var sheet = workbook.AddSheet("Executive Summary");
        sheet.SetHeader(new[] { "Item", "Value", "Status" });

        AddSummaryRow(sheet, "Solution", solution.SolutionName, "Completed");
        AddSummaryRow(sheet, "Solution Path", solution.SolutionPath, "Completed");
        AddSummaryRow(sheet, "Analysis Timestamp", $"{(solution.AnalyzedAtUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC")}", "Completed");
        AddSummaryRow(sheet, "Projects Analyzed", solution.TotalProjects, "Completed");
        AddSummaryRow(sheet, "Projects With Issues", solution.ProjectsWithIssues,
            solution.ProjectsWithIssues > 0 ? "Needs Review" : "Completed");
        AddSummaryRow(sheet, "Projects Not Analyzed", solution.ProjectsNotAnalyzed,
            solution.ProjectsNotAnalyzed > 0 ? "Needs Review" : "Completed");
        AddSummaryRow(sheet, "Projects Without Session Usage", solution.ProjectsWithoutSessionUsage, "Completed");
        AddSummaryRow(sheet, "Documents Analyzed", solution.TotalDocuments, "Completed");
        AddSummaryRow(sheet, "Controllers Discovered", solution.TotalControllers, "Completed");
        AddSummaryRow(sheet, "Actions Analyzed", solution.TotalActions, "Completed");
        AddSummaryRow(sheet, "Actions Using Session", solution.TotalActionsWithSession, "Completed");
        AddSummaryRow(sheet, "Session Operations Detected", solution.TotalSessionOperations, "Completed");
        AddSummaryRow(sheet, "Rejected Session Detections", solution.TotalRejectedDetections,
            solution.TotalRejectedDetections > 0 ? "Needs Review" : "Completed");
        AddSummaryRow(sheet, "Diagnostics", solution.TotalDiagnostics,
            solution.TotalDiagnostics > 0 ? "Needs Review" : "Completed");
        AddSummaryRow(sheet, "Compilation Errors", solution.TotalCompilationErrors,
            solution.TotalCompilationErrors > 0 ? "Needs Review" : "Completed");
        AddSummaryRow(sheet, "Compilation Warnings", solution.TotalWarnings,
            solution.TotalWarnings > 0 ? "Needs Review" : "Completed");

        sheet.AddTextRule(2, "Needs Review", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(2, "Completed", XlsxWorkbook.DxfLow);
    }

    private static void AddSummaryRow(XlsxSheet sheet, string item, object value, string status)
    {
        sheet.AddRow(item, value, status);
    }

    // ------------------------------------------------------------------
    // Sheet 2: Project Analysis
    // ------------------------------------------------------------------

    private static void WriteProjectAnalysis(XlsxWorkbook workbook, IReadOnlyList<ProjectSummaryModel> projects)
    {
        var sheet = workbook.AddSheet("Project Analysis");
        sheet.SetHeader(new[]
        {
            "Priority", "Project", "Path", "Documents", "Controllers", "Actions",
            "Session Actions", "Session Operations", "Compilation Errors", "Status", "Developer Action"
        });

        foreach (var project in projects)
        {
            sheet.AddRow(
                project.Priority,
                project.ProjectName,
                project.ProjectPath,
                project.Documents,
                project.Controllers,
                project.Actions,
                project.SessionActions,
                project.SessionOperations,
                project.CompilationErrors,
                project.Status,
                project.DeveloperAction);
        }

        sheet.SetColumnStyle(10, XlsxWorkbook.StyleWrapText);

        sheet.AddTextRule(0, "High", XlsxWorkbook.DxfHigh);
        sheet.AddTextRule(0, "Medium", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(0, "Low", XlsxWorkbook.DxfLow);

        sheet.AddTextRule(9, "Needs Review", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(9, "Not Analyzed", XlsxWorkbook.DxfHigh);
        sheet.AddTextRule(9, "No Session Usage", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(9, "Clean", XlsxWorkbook.DxfLow);

        sheet.AddNumberRule(8, "greaterThan", 0, XlsxWorkbook.DxfProblem);
    }

    // ------------------------------------------------------------------
    // Sheet 3: Session Findings
    // ------------------------------------------------------------------

    private static void WriteSessionFindings(XlsxWorkbook workbook, IReadOnlyList<SessionFindingModel> findings)
    {
        var sheet = workbook.AddSheet("Session Findings");
        sheet.SetHeader(new[]
        {
            "Project", "Controller", "Action", "File", "Line", "Operation",
            "Session Key", "Type", "Confidence", "Status", "Developer Notes"
        });

        foreach (var finding in findings)
        {
            var notes = BuildFindingNotes(finding);
            sheet.AddRow(
                finding.Project,
                finding.Controller,
                finding.Action,
                finding.File,
                finding.Line,
                finding.Operation,
                finding.SessionKey,
                finding.SessionType,
                finding.Confidence,
                "Open",
                notes);
        }

        sheet.SetColumnStyle(10, XlsxWorkbook.StyleWrapText);
        sheet.AddTextRule(9, "Open", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(9, "Completed", XlsxWorkbook.DxfLow);
    }

    private static string BuildFindingNotes(SessionFindingModel finding)
    {
        var parts = new List<string> { finding.Expression };
        if (!string.IsNullOrEmpty(finding.Mutation) &&
            !string.Equals(finding.Mutation, finding.Operation, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"mutation: {finding.Mutation}");
        }

        parts.Add($"access path: {finding.AccessPath}");
        if (!string.IsNullOrEmpty(finding.Note))
        {
            parts.Add(finding.Note);
        }

        return string.Join(" | ", parts);
    }

    // ------------------------------------------------------------------
    // Sheet 4: Diagnostics
    // ------------------------------------------------------------------

    private static void WriteDiagnostics(XlsxWorkbook workbook, IReadOnlyList<DiagnosticItemModel> diagnostics)
    {
        var sheet = workbook.AddSheet("Diagnostics");
        sheet.SetHeader(new[]
        {
            "Priority", "Project", "Category", "Message", "File", "Line",
            "Impact", "Suggested Fix", "Status"
        });

        foreach (var diagnostic in diagnostics)
        {
            sheet.AddRow(
                diagnostic.Priority,
                diagnostic.Project,
                diagnostic.Category,
                diagnostic.Message,
                diagnostic.File,
                diagnostic.Line > 0 ? (object)diagnostic.Line : "-",
                diagnostic.Impact,
                diagnostic.SuggestedFix,
                diagnostic.Status);
        }

        sheet.SetColumnStyle(3, XlsxWorkbook.StyleWrapText);
        sheet.SetColumnStyle(6, XlsxWorkbook.StyleWrapText);
        sheet.SetColumnStyle(7, XlsxWorkbook.StyleWrapText);

        sheet.AddTextRule(0, "High", XlsxWorkbook.DxfHigh);
        sheet.AddTextRule(0, "Medium", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(0, "Low", XlsxWorkbook.DxfLow);

        sheet.AddTextRule(8, "Open", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(8, "Resolved", XlsxWorkbook.DxfLow);
    }

    // ------------------------------------------------------------------
    // Sheet 5: Developer To-Do List
    // ------------------------------------------------------------------

    private static void WriteTodoList(XlsxWorkbook workbook, IReadOnlyList<TodoItemModel> todoItems)
    {
        var sheet = workbook.AddSheet("Developer To-Do List");
        sheet.SetHeader(new[]
        {
            "ID", "Priority", "Category", "Project", "Component", "Problem",
            "Evidence", "Suggested Solution", "Owner", "Status",
            "Created Date", "Completed Date", "Notes"
        });

        foreach (var todo in todoItems)
        {
            sheet.AddRow(
                todo.Id,
                todo.Priority,
                todo.Category,
                todo.Project,
                todo.Component,
                todo.Problem,
                todo.Evidence,
                todo.SuggestedSolution,
                todo.Owner,
                todo.Status,
                todo.CreatedDate,
                todo.CompletedDate,
                todo.Notes);
        }

        sheet.SetColumnStyle(5, XlsxWorkbook.StyleWrapText);
        sheet.SetColumnStyle(6, XlsxWorkbook.StyleWrapText);
        sheet.SetColumnStyle(7, XlsxWorkbook.StyleWrapText);
        sheet.SetColumnStyle(12, XlsxWorkbook.StyleWrapText);

        sheet.AddTextRule(1, "High", XlsxWorkbook.DxfHigh);
        sheet.AddTextRule(1, "Medium", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(1, "Low", XlsxWorkbook.DxfLow);

        sheet.AddTextRule(9, "Open", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(9, "In Progress", XlsxWorkbook.DxfMedium);
        sheet.AddTextRule(9, "Completed", XlsxWorkbook.DxfLow);
    }
}
