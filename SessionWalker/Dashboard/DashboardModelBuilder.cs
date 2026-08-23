using SessionWalker.Core.Models;
using SessionWalker.Dashboard.Models;
using System.Text.RegularExpressions;

namespace SessionWalker.Dashboard;

/// <summary>
/// Maps the analysis engine's <see cref="AnalysisResult"/> onto the
/// dashboard/export view model (<see cref="AnalysisDashboardModel"/>).
///
/// This is the single seam between the Roslyn pipeline and everything a
/// developer sees: overview numbers, project health, session findings,
/// investigation cases and generated to-do items are all computed here, once,
/// from the result of one analysis run. Neither the HTML dashboard nor the
/// Excel export ever triggers analysis or touches Roslyn types.
/// </summary>
public static class DashboardModelBuilder
{
    private const string NotAnalyzedStatus = "Not Analyzed";
    private const string NeedsReviewStatus = "Needs Review";
    private const string NoSessionUsageStatus = "No Session Usage";
    private const string CleanStatus = "Clean";

    private const string High = "High";
    private const string Medium = "Medium";
    private const string Low = "Low";

    private static readonly Regex CompilerDiagnosticPattern = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<code>CS\d+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex QuotedNamePattern = new(
        @"'(?<name>[^']+)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnresolvedReferencePattern = new(
        @"\[Unresolved reference\]\s+'(?<name>[^']+)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnalyzerFailurePattern = new(
        @"Analyzer\s+'(?<analyzer>[^']+)'\s+failed on project\s+'(?<project>[^']+)':\s*(?<message>.*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AnalysisDashboardModel Build(AnalysisResult result)
    {
        var canBeReadOnly = BuildCanBeReadOnlyLookup(result);
        var findings = BuildSessionFindings(result, canBeReadOnly);
        var diagnostics = BuildDiagnostics(result);
        var investigations = BuildInvestigations(result);
        var todoItems = BuildTodoItems(result, investigations, result.AnalyzedAtUtc);
        var projects = BuildProjectSummaries(result, todoItems, canBeReadOnly);
        var solution = BuildSolutionSummary(result, projects);

        return new AnalysisDashboardModel(solution, projects, findings, diagnostics, investigations, todoItems);
    }

    // ------------------------------------------------------------------
    // Solution summary
    // ------------------------------------------------------------------

    private static SolutionSummaryModel BuildSolutionSummary(
        AnalysisResult result,
        IReadOnlyList<ProjectSummaryModel> projects)
    {
        var totalProjects = projects.Count;
        var totalDocuments = projects.Sum(p => p.Documents);
        var totalControllers = projects.Sum(p => p.Controllers);
        var totalActions = projects.Sum(p => p.Actions);
        var totalSessionActions = projects.Sum(p => p.SessionActions);
        var totalSessionOperations = projects.Sum(p => p.SessionOperations);
        var totalDiagnostics = result.Projects.Sum(p => p.Diagnostics.Count);
        var totalErrors = result.Projects.Sum(p => p.CompilationErrorCount);
        var totalWarnings = result.Projects.Sum(p => p.CompilationWarningCount);
        var totalRejected = result.Projects.Sum(p => p.RejectedCandidateList.Count);
        var projectsWithIssues = projects.Count(p =>
            p.Status is NeedsReviewStatus or NotAnalyzedStatus);
        var projectsWithoutSession = projects.Count(p => p.Status == NoSessionUsageStatus);
        var projectsNotAnalyzed = projects.Count(p => p.Status == NotAnalyzedStatus);

        return new SolutionSummaryModel(
            SolutionName: Path.GetFileNameWithoutExtension(result.SolutionPath),
            SolutionPath: result.SolutionPath,
            AnalyzedAtUtc: result.AnalyzedAtUtc,
            TotalProjects: totalProjects,
            TotalDocuments: totalDocuments,
            TotalControllers: totalControllers,
            TotalActions: totalActions,
            TotalActionsWithSession: totalSessionActions,
            TotalSessionOperations: totalSessionOperations,
            TotalDiagnostics: totalDiagnostics,
            TotalCompilationErrors: totalErrors,
            TotalWarnings: totalWarnings,
            TotalRejectedDetections: totalRejected,
            ProjectsWithIssues: projectsWithIssues,
            ProjectsWithoutSessionUsage: projectsWithoutSession,
            ProjectsNotAnalyzed: projectsNotAnalyzed);
    }

    // ------------------------------------------------------------------
    // Project health
    // ------------------------------------------------------------------

    private static IReadOnlyList<ProjectSummaryModel> BuildProjectSummaries(
        AnalysisResult result,
        IReadOnlyList<TodoItemModel> todoItems,
        IReadOnlyDictionary<(string, string, string), bool> canBeReadOnly)
    {
        var highestPriorityTodo = todoItems
            .GroupBy(t => t.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(t => PriorityRank(t.Priority)).First());

        var summaries = new List<ProjectSummaryModel>();

        foreach (var project in result.Projects)
        {
            var (status, priority, fallbackAction) = ClassifyProject(project);
            var action = highestPriorityTodo.TryGetValue(project.ProjectName, out var todo)
                ? todo.SuggestedSolution
                : fallbackAction;

            summaries.Add(new ProjectSummaryModel(
                ProjectName: project.ProjectName,
                AssemblyName: project.AssemblyName,
                ProjectPath: ProjectDirectory(project.ProjectFilePath),
                Documents: project.DocumentCount,
                Controllers: project.Controllers.Count,
                Actions: project.Controllers.Sum(c => c.Actions.Count),
                SessionActions: project.Controllers
                    .SelectMany(c => c.Actions)
                    .Count(a => a.UsesSession),
                SessionOperations: project.AllSessionOperations.Count,
                CompilationErrors: project.CompilationErrorCount,
                Warnings: project.CompilationWarningCount,
                Status: status,
                Priority: priority,
                DeveloperAction: action,
                HasSessionUsage: project.AllSessionOperations.Count > 0,
                HasRejectedDetections: project.RejectedCandidateList.Count > 0,
                IsAnalyzed: project.CompilationSucceeded,
                ControllerDetails: BuildControllerDetails(project, canBeReadOnly)));
        }

        return summaries
            .OrderBy(s => PriorityRank(s.Priority))
            .ThenBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ControllerDetailModel> BuildControllerDetails(
        ProjectAnalysisResult project,
        IReadOnlyDictionary<(string, string, string), bool> canBeReadOnly)
    {
        return project.Controllers
            .Select(controller => new ControllerDetailModel(
                Controller: controller.ControllerName,
                File: controller.FilePath,
                Actions: controller.Actions
                    .Select(action =>
                    {
                        var operations = action.Operations
                            .Select(op => ToSessionFinding(op, canBeReadOnly))
                            .ToList();
                        return new ActionDetailModel(
                            Action: action.ActionName,
                            File: action.Location.FilePath,
                            Line: action.Location.Line,
                            Column: action.Location.Column,
                            SessionOperations: operations.Count,
                            Reads: action.SessionReadCount,
                            Writes: action.SessionWriteCount,
                            CanBeReadOnly: action.CanPotentiallyUseReadOnlySession,
                            Operations: operations);
                    })
                    .ToList()))
            .OrderBy(c => c.Controller, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (string Status, string Priority, string Action) ClassifyProject(ProjectAnalysisResult project)
    {
        if (!project.CompilationSucceeded)
        {
            return (NotAnalyzedStatus, High, "Fix the project load/compilation failure and re-run analysis.");
        }

        if (project.CompilationErrorCount > 0)
        {
            return (NeedsReviewStatus, High,
                $"Resolve {project.CompilationErrorCount} compilation error(s) so analysis can bind types correctly.");
        }

        if (project.CompilationWarningCount > 0)
        {
            return (NeedsReviewStatus, Medium,
                $"Resolve {project.CompilationWarningCount} compilation warning(s).");
        }

        if (project.RejectedCandidateList.Count > 0)
        {
            return (NeedsReviewStatus, Medium,
                $"Review {project.RejectedCandidateList.Count} Session-like access(es) that were not detected.");
        }

        if (project.Controllers.Count > 0 && project.AllSessionOperations.Count == 0)
        {
            return (NoSessionUsageStatus, Medium,
                "Confirm there is really no Session usage; the analyzer may have missed a pattern.");
        }

        return (CleanStatus, Low, "No action needed.");
    }

    // ------------------------------------------------------------------
    // Session findings
    // ------------------------------------------------------------------

    private static IReadOnlyDictionary<(string, string, string), bool> BuildCanBeReadOnlyLookup(AnalysisResult result)
    {
        return result.AllActions
            .GroupBy(a => (a.ProjectName, a.ControllerName, a.ActionName))
            .ToDictionary(g => g.Key, g => g.First().CanPotentiallyUseReadOnlySession);
    }

    private static IReadOnlyList<SessionFindingModel> BuildSessionFindings(
        AnalysisResult result,
        IReadOnlyDictionary<(string, string, string), bool> canBeReadOnly)
    {
        var findings = result.AllSessionOperations
            .Select(op => ToSessionFinding(op, canBeReadOnly))
            .ToList();

        return findings
            .OrderBy(f => f.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Controller, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Action, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ToList();
    }

    private static SessionFindingModel ToSessionFinding(
        SessionOperationResult op,
        IReadOnlyDictionary<(string, string, string), bool> canBeReadOnly)
    {
        var canBeReadOnlyText = "N/A";
        if (op.Symbol.ControllerName is not null && op.Symbol.ActionName is not null)
        {
            var key = (op.Symbol.ProjectName ?? string.Empty, op.Symbol.ControllerName, op.Symbol.ActionName);
            if (canBeReadOnly.TryGetValue(key, out var eligible))
            {
                canBeReadOnlyText = eligible ? "YES" : "NO";
            }
        }

        return new SessionFindingModel(
            Project: op.Symbol.ProjectName ?? "-",
            Controller: op.Symbol.ControllerName ?? "-",
            Action: op.Symbol.ActionName ?? "-",
            File: op.Location.FilePath,
            Line: op.Location.Line,
            Column: op.Location.Column,
            Operation: op.OperationType.ToString(),
            Mutation: op.MutationKind?.ToString() ?? string.Empty,
            SessionKey: op.Key ?? string.Empty,
            SessionType: op.IsWrite ? "Write" : "Read",
            DetectionMethod: DescribeDetectionMethod(op),
            Confidence: op.Confidence.ToString(),
            Expression: op.Expression ?? string.Empty,
            AccessPath: op.AccessPath.ToString(),
            Note: op.Note ?? string.Empty,
            CanBeReadOnly: canBeReadOnlyText);
    }

    private static string DescribeDetectionMethod(SessionOperationResult op)
    {
        return op.AccessPath switch
        {
            AccessPath.LocalVariableIndirection => "Semantic Symbol Detection (local variable)",
            AccessPath.MethodParameter => "Semantic Symbol Detection (method parameter)",
            AccessPath.InterproceduralCall => "Interprocedural Trace",
            AccessPath.PropertySetter => "Property Setter Indirection",
            _ => "Semantic Symbol Detection"
        };
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    private static IReadOnlyList<DiagnosticItemModel> BuildDiagnostics(AnalysisResult result)
    {
        var items = new List<DiagnosticItemModel>();

        foreach (var project in result.Projects)
        {
            foreach (var diagnostic in project.Diagnostics)
            {
                items.Add(ClassifyDiagnostic(project.ProjectName, diagnostic));
            }

            foreach (var warning in project.WarningList)
            {
                items.Add(ClassifyDiagnostic(project.ProjectName, warning));
            }

            foreach (var rejected in project.RejectedCandidateList)
            {
                items.Add(ClassifyRejectedCandidate(project.ProjectName, rejected));
            }
        }

        return items
            .OrderBy(d => PriorityRank(d.Priority))
            .ThenBy(d => d.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ToList();
    }

    private static DiagnosticItemModel ClassifyDiagnostic(string projectName, string text)
    {
        if (TryParseCompilerDiagnostic(text, out var severity, out var code, out var message, out var file, out var line, out var column))
        {
            var isError = string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase);
            var category = ClassifyCompilerCode(code, isError);
            var priority = isError ? High : Low;

            return new DiagnosticItemModel(
                Priority: priority,
                Project: projectName,
                Category: category,
                Message: message,
                File: file,
                Line: line,
                Column: column,
                Impact: ImpactFor(category),
                SuggestedFix: FixFor(code, category, message),
                Status: "Open");
        }

        if (UnresolvedReferencePattern.IsMatch(text))
        {
            var name = UnresolvedReferencePattern.Match(text).Groups["name"].Value;
            return new DiagnosticItemModel(
                Priority: High,
                Project: projectName,
                Category: "Reference Error",
                Message: text,
                File: "-",
                Line: 0,
                Column: 0,
                Impact: "Missing assembly reference; types from it bind to error types and Session usage behind it may be missed.",
                SuggestedFix: $"Restore the '{name}' reference (NuGet restore, HintPath, or add the package).",
                Status: "Open");
        }

        if (text.StartsWith("[Missing project reference]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Unreadable reference]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[No references resolved]", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticItemModel(
                Priority: High,
                Project: projectName,
                Category: "Reference Error",
                Message: text,
                File: "-",
                Line: 0,
                Column: 0,
                Impact: "Compilation incomplete; semantic results and Session detection will be limited.",
                SuggestedFix: "Restore the referenced project/assembly and re-run analysis.",
                Status: "Open");
        }

        if (text.StartsWith("[Missing project]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Missing source]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Unreadable source]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Failed to load]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[No projects]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Failed to read syntax tree", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticItemModel(
                Priority: Medium,
                Project: projectName,
                Category: "Load Error",
                Message: text,
                File: "-",
                Line: 0,
                Column: 0,
                Impact: "Part of the project could not be loaded; analysis coverage is incomplete.",
                SuggestedFix: "Fix the loading problem (missing file, unreadable source, unsupported project) and re-run.",
                Status: "Open");
        }

        if (text.StartsWith("Failed to compile project", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("produced no compilation", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticItemModel(
                Priority: High,
                Project: projectName,
                Category: "Compilation Error",
                Message: text,
                File: "-",
                Line: 0,
                Column: 0,
                Impact: "Project was not analyzed; all Session results for it are missing.",
                SuggestedFix: "Fix the compilation failure and re-run analysis.",
                Status: "Open");
        }

        if (AnalyzerFailurePattern.IsMatch(text))
        {
            return new DiagnosticItemModel(
                Priority: High,
                Project: projectName,
                Category: "Analyzer Error",
                Message: text,
                File: "-",
                Line: 0,
                Column: 0,
                Impact: "An analyzer failed; its findings are incomplete for this project.",
                SuggestedFix: "Review the analyzer exception and report a bug if the analyzer itself is at fault.",
                Status: "Open");
        }

        if (text.StartsWith("Interprocedural trace failed", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticItemModel(
                Priority: Medium,
                Project: projectName,
                Category: "Analyzer Warning",
                Message: text,
                File: "-",
                Line: 0,
                Column: 0,
                Impact: "One call chain could not be traced; indirect Session usage may be under-reported.",
                SuggestedFix: "Simplify the helper call chain or verify the flagged call site manually.",
                Status: "Open");
        }

        return new DiagnosticItemModel(
            Priority: Medium,
            Project: projectName,
            Category: "Other",
            Message: text,
            File: "-",
            Line: 0,
            Column: 0,
            Impact: "See the message for context.",
            SuggestedFix: "Investigate and re-run analysis if the message indicates a tooling problem.",
            Status: "Open");
    }

    private static DiagnosticItemModel ClassifyRejectedCandidate(string projectName, RejectedCandidate rejected)
    {
        var unsupported = rejected.Reason == SessionTypeRejectionReason.UnsupportedSessionTypeMapping;
        var typeLabel = string.IsNullOrEmpty(rejected.ResolvedType) ? "<unknown>" : rejected.ResolvedType;

        return new DiagnosticItemModel(
            Priority: unsupported ? High : Medium,
            Project: projectName,
            Category: "Detection Gap",
            Message: $"Session-like access rejected: '{rejected.Expression}' resolved to {typeLabel} ({DescribeReason(rejected.Reason)}).",
            File: rejected.Location.FilePath,
            Line: rejected.Location.Line,
            Column: rejected.Location.Column,
            Impact: "Possible missed Session operation; results may under-report Session usage.",
            SuggestedFix: unsupported
                ? $"Add '{typeLabel}' to the supported Session type mapping in SessionSymbolDetector."
                : "Resolve the missing reference so this expression can bind to a known Session type.",
            Status: "Open");
    }

    private static string ClassifyCompilerCode(string code, bool isError)
    {
        if (code is "CS1703" or "CS1704")
        {
            return "Duplicate Assembly";
        }

        if (isError && code is "CS0246" or "CS0234" or "CS0012" or "CS0400" or "CS1705")
        {
            return "Reference Error";
        }

        return isError ? "Compilation Error" : "Compilation Warning";
    }

    private static string ImpactFor(string category)
    {
        return category switch
        {
            "Reference Error" => "Compilation incomplete; types may not bind and Session usage may be missed.",
            "Duplicate Assembly" => "Ambiguous assembly; binding may pick the wrong type or fail entirely.",
            "Compilation Error" => "Compilation incomplete; analysis coverage may be partial.",
            "Compilation Warning" => "Potential runtime issue; may affect analysis accuracy.",
            "Analyzer Error" => "Analyzer did not complete; findings may be missing.",
            "Load Error" => "Project/sources could not be loaded; analysis coverage incomplete.",
            "Detection Gap" => "Known Session-looking access was NOT detected.",
            _ => "See the message for context."
        };
    }

    private static string FixFor(string code, string category, string message)
    {
        if (code is "CS1703" or "CS1704")
        {
            var name = ExtractQuotedName(message);
            return string.IsNullOrEmpty(name)
                ? "Remove the duplicate assembly reference and keep a single version."
                : $"Remove the duplicate '{name}' assembly reference and keep a single version.";
        }

        if (category == "Reference Error")
        {
            var name = ExtractQuotedName(message);
            return string.IsNullOrEmpty(name)
                ? "Restore the missing package/reference (NuGet restore or HintPath)."
                : $"Restore the missing '{name}' reference (NuGet restore or HintPath).";
        }

        if (category == "Compilation Warning")
        {
            return "Fix the compiler warning so the build is clean and analysis is accurate.";
        }

        return "Fix the reported problem and re-run analysis.";
    }

    // ------------------------------------------------------------------
    // Investigations
    // ------------------------------------------------------------------

    private static IReadOnlyList<InvestigationCaseModel> BuildInvestigations(AnalysisResult result)
    {
        var cases = new List<InvestigationCaseModel>();

        foreach (var project in result.Projects)
        {
            cases.AddRange(BuildCompilationCases(project));
            cases.AddRange(BuildRejectionCases(project));

            var noSessionCase = BuildNoSessionCase(project);
            if (noSessionCase is not null)
            {
                cases.Add(noSessionCase);
            }

            cases.AddRange(BuildAnalyzerFailureCases(project));
        }

        return cases
            .OrderBy(c => SeverityRank(c.Severity))
            .ThenBy(c => c.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<InvestigationCaseModel> BuildCompilationCases(ProjectAnalysisResult project)
    {
        var problems = new List<string>();
        var hasMissingReference = false;
        var hasDuplicateAssembly = false;
        var hasSourceError = false;
        var hasLoadProblem = false;

        if (!project.CompilationSucceeded)
        {
            hasLoadProblem = true;
            problems.Add(project.Diagnostics.Count > 0
                ? project.Diagnostics[0]
                : "Compilation did not produce an analyzable compilation.");
        }

        foreach (var diagnostic in project.Diagnostics)
        {
            if (TryParseCompilerDiagnostic(diagnostic, out _, out var code, out var message, out _, out _, out _))
            {
                if (code is "CS1703" or "CS1704")
                {
                    hasDuplicateAssembly = true;
                    var name = ExtractQuotedName(message);
                    problems.Add($"Duplicate Assembly: {(string.IsNullOrEmpty(name) ? code : name)}");
                }
                else if (code is "CS0246" or "CS0234" or "CS0012" or "CS0400" or "CS1705")
                {
                    hasMissingReference = true;
                    var name = ExtractQuotedName(message);
                    problems.Add($"Missing Reference: {(string.IsNullOrEmpty(name) ? code : name)}");
                }
                else
                {
                    hasSourceError = true;
                    problems.Add($"Compilation Error ({code}): {message}");
                }

                continue;
            }

            if (UnresolvedReferencePattern.IsMatch(diagnostic))
            {
                hasMissingReference = true;
                problems.Add($"Missing Reference: {UnresolvedReferencePattern.Match(diagnostic).Groups["name"].Value}");
                continue;
            }

            if (diagnostic.StartsWith("[Missing project reference]", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.StartsWith("[Failed to load]", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.StartsWith("[Unreadable source]", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.StartsWith("[Missing source]", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.StartsWith("[No references resolved]", StringComparison.OrdinalIgnoreCase))
            {
                hasLoadProblem = true;
                problems.Add($"Project Load: {diagnostic}");
            }
        }

        if (problems.Count == 0)
        {
            return Array.Empty<InvestigationCaseModel>();
        }

        var reasons = new List<string>();
        if (hasMissingReference) reasons.Add("Missing references");
        if (hasDuplicateAssembly) reasons.Add("Duplicate assemblies");
        if (hasSourceError) reasons.Add("Source code errors");
        if (hasLoadProblem) reasons.Add("Project/loading problem; compilation incomplete");

        var notAnalyzed = !project.CompilationSucceeded ||
                          (project.CompilationErrorCount == 0 && project.Controllers.Count == 0 && hasLoadProblem);

        return new[]
        {
            new InvestigationCaseModel(
                Kind: InvestigationKind.CompilationProblem,
                Title: notAnalyzed
                    ? $"Project '{project.ProjectName}' could not be analyzed"
                    : $"Compilation problems in '{project.ProjectName}'",
                Project: project.ProjectName,
                Component: "Project",
                File: "-",
                Severity: project.CompilationErrorCount > 0 || !project.CompilationSucceeded ? High : Medium,
                Evidence: problems.Take(15).ToList(),
                PossibleReasons: reasons,
                SuggestedAction: "Fix the reported compiler/load problems, then re-run analysis.",
                SuggestedFix: "Resolve the reference/compile problems listed above; re-run after the build is clean.")
        };
    }

    private static IReadOnlyList<InvestigationCaseModel> BuildRejectionCases(ProjectAnalysisResult project)
    {
        var cases = new List<InvestigationCaseModel>();

        var groups = project.RejectedCandidateList
            .GroupBy(r => (r.ResolvedType, r.Reason))
            .OrderByDescending(g => g.Key.Reason == SessionTypeRejectionReason.UnsupportedSessionTypeMapping)
            .ThenByDescending(g => g.Count());

        foreach (var group in groups)
        {
            var unsupported = group.Key.Reason == SessionTypeRejectionReason.UnsupportedSessionTypeMapping;
            var typeLabel = string.IsNullOrEmpty(group.Key.ResolvedType) ? "<unknown>" : group.Key.ResolvedType;
            var examples = group
                .Take(5)
                .Select(r => $"{r.Expression} — {r.Location.FilePath}:{r.Location.Line} " +
                             $"({r.Symbol.ControllerName ?? r.Symbol.ClassName ?? r.Symbol.MethodName ?? "?"})")
                .ToList();

            if (group.Count() > examples.Count)
            {
                examples.Add($"... and {group.Count() - examples.Count} more occurrence(s)");
            }

            var reasons = unsupported
                ? new List<string>
                {
                    "Unsupported Session type mapping",
                    "Custom wrapper/derived Session type not in the known mapping",
                    "Type refactored (e.g. moved namespace) without updating the mapping"
                }
                : new List<string>
                {
                    "Type could not be resolved (error type / missing reference)",
                    "Expression is dynamic or late-bound",
                    "Project reference not resolved during loading"
                };

            cases.Add(new InvestigationCaseModel(
                Kind: InvestigationKind.RejectedDetection,
                Title: $"Session access rejected: {typeLabel}",
                Project: project.ProjectName,
                Component: "SessionSymbolDetector",
                File: group.First().Location.FilePath,
                Severity: unsupported ? High : Medium,
                Evidence: examples,
                PossibleReasons: reasons,
                SuggestedAction: "Decide whether this expression really is Session access; if so, extend the detector mapping.",
                SuggestedFix: unsupported
                    ? $"Add '{typeLabel}' to the known Session type mapping in SessionSymbolDetector."
                    : "Resolve the reference so the expression type binds to a known Session type."));
        }

        return cases;
    }

    private static InvestigationCaseModel? BuildNoSessionCase(ProjectAnalysisResult project)
    {
        // Only meaningful for projects the analyzer could actually look at and
        // that appear to be MVC projects (controllers were discovered).
        if (!project.CompilationSucceeded ||
            project.CompilationErrorCount > 0 ||
            project.Controllers.Count == 0 ||
            project.AllSessionOperations.Count > 0)
        {
            return null;
        }

        var actionCount = project.Controllers.Sum(c => c.Actions.Count);
        var evidence = new List<string>
        {
            $"Documents scanned: {project.DocumentCount}",
            $"Controllers: {project.Controllers.Count}",
            $"Actions scanned: {actionCount}",
            $"Session operations: 0"
        };

        var reasons = new List<string> { "No Session usage found" };
        if (project.RejectedCandidateList.Count > 0)
        {
            reasons.Add("Unsupported pattern (Session-like access was rejected; see Rejected Detection cases)");
            evidence.Add($"Rejected Session-like accesses: {project.RejectedCandidateList.Count}");
        }

        var hasReferenceProblem = project.Diagnostics.Any(d =>
            UnresolvedReferencePattern.IsMatch(d) ||
            d.StartsWith("[Missing project reference]", StringComparison.OrdinalIgnoreCase) ||
            d.StartsWith("[No references resolved]", StringComparison.OrdinalIgnoreCase) ||
            (TryParseCompilerDiagnostic(d, out _, out var code, out _, out _, out _, out _) &&
             code is "CS0246" or "CS0234" or "CS0012"));
        if (hasReferenceProblem)
        {
            reasons.Add("Missing references");
        }

        if (project.CompilationWarningCount > 0)
        {
            reasons.Add("Compilation incomplete (warnings present)");
        }

        return new InvestigationCaseModel(
            Kind: InvestigationKind.NoSessionDetection,
            Title: $"No Session usage detected in '{project.ProjectName}'",
            Project: project.ProjectName,
            Component: "Session Detection",
            File: "-",
            Severity: Medium,
            Evidence: evidence,
            PossibleReasons: reasons,
            SuggestedAction: "Confirm the negative result before concluding the project is Session-free.",
            SuggestedFix: "Search for Session usage targets, review helper/extension patterns, and add a regression sample if the analyzer missed a real Session access.");
    }

    private static IReadOnlyList<InvestigationCaseModel> BuildAnalyzerFailureCases(ProjectAnalysisResult project)
    {
        var cases = new List<InvestigationCaseModel>();

        foreach (var diagnostic in project.Diagnostics)
        {
            var match = AnalyzerFailurePattern.Match(diagnostic);
            if (!match.Success)
            {
                continue;
            }

            var analyzerName = match.Groups["analyzer"].Value;
            cases.Add(new InvestigationCaseModel(
                Kind: InvestigationKind.AnalyzerFailure,
                Title: $"Analyzer '{analyzerName}' failed on '{project.ProjectName}'",
                Project: project.ProjectName,
                Component: analyzerName,
                File: "-",
                Severity: High,
                Evidence: new[] { match.Groups["message"].Value },
                PossibleReasons: new[] { "Analyzer bug", "Unexpected source construct", "Resource/stack limit" },
                SuggestedAction: "Review the exception; confirm whether detection results for this project are incomplete.",
                SuggestedFix: "Fix the analyzer or file a bug with the reproduction; re-run analysis."));
        }

        return cases;
    }

    // ------------------------------------------------------------------
    // To-do list
    // ------------------------------------------------------------------

    private enum ReferenceProblemKind
    {
        Missing,
        Duplicate
    }

    private sealed record ReferenceProblem(
        string Name,
        ReferenceProblemKind Kind,
        string Evidence)
    {
        public string Name { get; } = Name;
        public ReferenceProblemKind Kind { get; } = Kind;
        public string Evidence { get; } = Evidence;
    }

    private sealed record TodoInput(
        string Priority,
        string Category,
        string Project,
        string Component,
        string Problem,
        string Evidence,
        string Solution,
        string Notes)
    {
        public string Priority { get; } = Priority;
        public string Category { get; } = Category;
        public string Project { get; } = Project;
        public string Component { get; } = Component;
        public string Problem { get; } = Problem;
        public string Evidence { get; } = Evidence;
        public string Solution { get; } = Solution;
        public string Notes { get; } = Notes;
    }

    private static IReadOnlyList<TodoItemModel> BuildTodoItems(
        AnalysisResult result,
        IReadOnlyList<InvestigationCaseModel> investigations,
        DateTimeOffset createdAt)
    {
        var candidates = new List<TodoInput>();

        foreach (var project in result.Projects)
        {
            //foreach (var reference in CollectReferenceProblems(project))
            //{
            //    var duplicate = reference.Kind == ReferenceProblemKind.Duplicate;
            //    candidates.Add(new TodoInput(
            //        Priority: High,
            //        Category: "Reference Issue",
            //        Project: project.ProjectName,
            //        Component: reference.Name,
            //        Problem: duplicate
            //            ? $"Duplicate assembly '{reference.Name}' causes ambiguous binding"
            //            : $"Reference '{reference.Name}' could not be resolved",
            //        Evidence: reference.Evidence,
            //        Solution: duplicate
            //            ? "Remove the duplicate reference or align assembly versions."
            //            : $"Restore the '{reference.Name}' reference (NuGet restore / HintPath).",
            //        Notes: "Blocking-type issue: detection may be incomplete until resolved."));
            //}

            //if (!project.CompilationSucceeded)
            //{
            //    candidates.Add(new TodoInput(
            //        Priority: High,
            //        Category: "Compilation Issue",
            //        Project: project.ProjectName,
            //        Component: "Project",
            //        Problem: $"Project '{project.ProjectName}' could not be analyzed",
            //        Evidence: project.Diagnostics.FirstOrDefault() ?? "No compilation was produced.",
            //        Solution: "Fix the load/compilation failure and re-run analysis.",
            //        Notes: "All Session results for this project are missing."));
            //}
            //else if (project.CompilationErrorCount > 0)
            //{
            //    candidates.Add(new TodoInput(
            //        Priority: Medium,
            //        Category: "Compilation Issue",
            //        Project: project.ProjectName,
            //        Component: "Project",
            //        Problem: $"{project.CompilationErrorCount} compilation error(s) limit analysis accuracy",
            //        Evidence: string.Join(" | ", project.Diagnostics.Take(3)),
            //        Solution: "Fix the compiler errors and re-run analysis.",
            //        Notes: "Types that do not bind cannot be recognized as Session types."));
            //}

            //if (project.CompilationWarningCount > 0)
            //{
            //    candidates.Add(new TodoInput(
            //        Priority: Low,
            //        Category: "Compilation Cleanup",
            //        Project: project.ProjectName,
            //        Component: "Project",
            //        Problem: $"{project.CompilationWarningCount} compilation warning(s) present",
            //        Evidence: string.Join(" | ", project.WarningList.Take(3)),
            //        Solution: "Resolve the warnings for a clean build.",
            //        Notes: "Warnings can hide binding problems that affect detection."));
            //}

            //var unsupported = project.RejectedCandidateList
            //    .Where(r => r.Reason == SessionTypeRejectionReason.UnsupportedSessionTypeMapping)
            //    .GroupBy(r => r.ResolvedType);
            //foreach (var group in unsupported)
            //{
            //    var typeLabel = string.IsNullOrEmpty(group.Key) ? "<unknown>" : group.Key;
            //    candidates.Add(new TodoInput(
            //        Priority: High,
            //        Category: "Analyzer Bug",
            //        Project: project.ProjectName,
            //        Component: "SessionSymbolDetector",
            //        Problem: $"'{typeLabel}' is not recognized as a Session type",
            //        Evidence: $"{group.First().Expression} — {group.First().Location.FilePath}:{group.First().Location.Line} " +
            //                  $"({group.Count()} occurrence(s))",
            //        Solution: $"Add '{typeLabel}' to the supported Session type mapping.",
            //        Notes: "Detection rule change requires separate review/approval."));
            //}

            //var unresolved = project.RejectedCandidateList
            //    .Where(r => r.Reason == SessionTypeRejectionReason.TypeNotResolved)
            //    .GroupBy(r => r.ResolvedType);
            //foreach (var group in unresolved)
            //{
            //    candidates.Add(new TodoInput(
            //        Priority: Medium,
            //        Category: "Reference Issue",
            //        Project: project.ProjectName,
            //        Component: "Reference Resolver",
            //        Problem: "Session-like access could not be bound to a Session type",
            //        Evidence: $"{group.First().Expression} — {group.First().Location.FilePath}:{group.First().Location.Line} " +
            //                  $"({group.Count()} occurrence(s))",
            //        Solution: "Resolve the reference/path so the expression binds to its real type.",
            //        Notes: "Once bound, re-run analysis to confirm detection."));
            //}

            if (project.CompilationSucceeded &&
                project.CompilationErrorCount == 0 &&
                project.Controllers.Count > 0 &&
                project.AllSessionOperations.Count == 0)
            {
                var actionCount = project.Controllers.Sum(c => c.Actions.Count);
                candidates.Add(new TodoInput(
                    Priority: Medium,
                    Category: "Manual Review",
                    Project: project.ProjectName,
                    Component: "Session Detection",
                    Problem: $"No Session usage detected in '{project.ProjectName}' — verify it is genuine",
                    Evidence: $"Documents: {project.DocumentCount}; Controllers: {project.Controllers.Count}; " +
                              $"Actions: {actionCount}; Session operations: 0",
                    Solution: "Confirm via searches/tests; extend the analyzer if this is a false negative.",
                    Notes: "A false negative hides real Session usage from ReadOnly eligibility checks."));
            }

            var lowConfidence = project.AllSessionOperations
                .Where(o => o.Confidence != ConfidenceLevel.High)
                .ToList();
            if (lowConfidence.Count > 0)
            {
                var first = lowConfidence
                    .OrderByDescending(o => o.Confidence) // least confident first
                    .First();
                candidates.Add(new TodoInput(
                    Priority: Low,
                    Category: "Manual Review",
                    Project: project.ProjectName,
                    Component: $"{first.Location.FilePath}:{first.Location.Line}",
                    Problem: $"{lowConfidence.Count} low/medium-confidence Session operation(s) need verification",
                    Evidence: $"{first.Expression} — {first.Location.FilePath}:{first.Location.Line}" +
                              (lowConfidence.Count > 1 ? $" (+{lowConfidence.Count - 1} more)" : string.Empty),
                    Solution: "Verify each operation at runtime and confirm the read/write classification.",
                    Notes: "Low-confidence results are never treated as definite writes."));
            }

            var readOnlyEligible = project.Controllers
                .SelectMany(c => c.Actions)
                .Count(a => a.UsesSession && a.CanPotentiallyUseReadOnlySession);
            if (readOnlyEligible > 0)
            {
                candidates.Add(new TodoInput(
                    Priority: Low,
                    Category: "Optimization",
                    Project: project.ProjectName,
                    Component: "Action",
                    Problem: $"{readOnlyEligible} action(s) may be eligible for SessionStateBehavior.ReadOnly",
                    Evidence: $"{readOnlyEligible} action(s) use Session with reads only across {project.Controllers.Count} controller(s).",
                    Solution: "Review eligibility and apply SessionStateBehavior where safe.",
                    Notes: "Only reads detected; always confirm before changing session behavior."));
            }
        }

        foreach (var failure in investigations.Where(i => i.Kind == InvestigationKind.AnalyzerFailure))
        {
            candidates.Add(new TodoInput(
                Priority: Medium,
                Category: "Analyzer Failure",
                Project: failure.Project,
                Component: failure.Component,
                Problem: $"Analyzer '{failure.Component}' failed; findings may be incomplete",
                Evidence: failure.Evidence.FirstOrDefault() ?? string.Empty,
                Solution: "Review the analyzer exception and fix or file a bug.",
                Notes: string.Empty));
        }

        // De-duplicate: keep the highest-priority input per (category, project, component, problem).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<TodoInput>();
        foreach (var candidate in candidates
                     .OrderBy(c => PriorityRank(c.Priority))
                     .ThenBy(c => c.Project, StringComparer.OrdinalIgnoreCase))
        {
            var key = $"{candidate.Category}\u0001{candidate.Project}\u0001{candidate.Component}\u0001{candidate.Problem}";
            if (seen.Add(key))
            {
                distinct.Add(candidate);
            }
        }

        var ordered = distinct
            .OrderBy(t => PriorityRank(t.Priority))
            .ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Component, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Problem, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<TodoItemModel>(ordered.Count);
        var index = 1;
        foreach (var todo in ordered)
        {
            items.Add(new TodoItemModel(
                Id: $"TASK-{index:000}",
                Priority: todo.Priority,
                Category: todo.Category,
                Project: todo.Project,
                Component: todo.Component,
                Problem: todo.Problem,
                Evidence: todo.Evidence,
                SuggestedSolution: todo.Solution,
                Owner: "Developer",
                Status: "Open",
                CreatedDate: createdAt,
                CompletedDate: null,
                Notes: todo.Notes));
            index++;
        }

        return items;
    }

    private static IReadOnlyList<ReferenceProblem> CollectReferenceProblems(ProjectAnalysisResult project)
    {
        var problems = new List<ReferenceProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var diagnostic in project.Diagnostics)
        {
            var match = UnresolvedReferencePattern.Match(diagnostic);
            if (match.Success)
            {
                AddReferenceProblem(problems, seen, match.Groups["name"].Value, ReferenceProblemKind.Missing, diagnostic);
                continue;
            }

            if (!TryParseCompilerDiagnostic(diagnostic, out _, out var code, out var message, out _, out _, out _))
            {
                continue;
            }

            if (code is "CS1703" or "CS1704")
            {
                AddReferenceProblem(problems, seen, ExtractQuotedName(message), ReferenceProblemKind.Duplicate, diagnostic);
            }
            else if (code is "CS0246" or "CS0234" or "CS0012" or "CS0400" or "CS1705")
            {
                AddReferenceProblem(problems, seen, ExtractQuotedName(message), ReferenceProblemKind.Missing, diagnostic);
            }
        }

        return problems;
    }

    private static void AddReferenceProblem(
        List<ReferenceProblem> problems,
        HashSet<string> seen,
        string name,
        ReferenceProblemKind kind,
        string evidence)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var key = $"{kind}\u0001{name}";
        if (seen.Add(key))
        {
            problems.Add(new ReferenceProblem(name, kind, evidence));
        }
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    private static bool TryParseCompilerDiagnostic(
        string text,
        out string severity,
        out string code,
        out string message,
        out string file,
        out int line,
        out int column)
    {
        severity = string.Empty;
        code = string.Empty;
        message = string.Empty;
        file = string.Empty;
        line = 0;
        column = 0;

        var match = CompilerDiagnosticPattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        severity = match.Groups["severity"].Value;
        code = match.Groups["code"].Value;
        message = match.Groups["message"].Value;
        file = match.Groups["file"].Value;
        line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
        column = int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static string ExtractQuotedName(string text)
    {
        var match = QuotedNamePattern.Match(text);
        return match.Success ? match.Groups["name"].Value : string.Empty;
    }

    private static string DescribeReason(SessionTypeRejectionReason reason) =>
        reason == SessionTypeRejectionReason.UnsupportedSessionTypeMapping
            ? "Unsupported Session type mapping"
            : "Type not resolved";

    private static string ProjectDirectory(string projectFilePath)
    {
        if (string.IsNullOrEmpty(projectFilePath))
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(projectFilePath) ?? projectFilePath;
    }

    private static int PriorityRank(string priority) =>
        string.Equals(priority, High, StringComparison.OrdinalIgnoreCase) ? 0 :
        string.Equals(priority, Medium, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static int SeverityRank(string severity) => PriorityRank(severity);
}
