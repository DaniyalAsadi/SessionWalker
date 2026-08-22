using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;
using System.Text;

namespace SessionWalker.Infrastructure.Output;

/// <summary>
/// Human-readable console report. Prints every operation grouped by
/// controller/action, followed by a ReadOnly-eligibility summary table.
/// </summary>
public sealed class ConsoleOutputWriter(TextWriter writer = null) : IOutputWriter
{
    public string FormatName => "console";

    private readonly TextWriter _writer = writer ?? Console.Out;

    public Task WriteAsync(AnalysisResult result, string destinationPath, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var divider = new string('=', 60);
        var thinDivider = new string('-', 60);

        sb.AppendLine(divider);
        sb.AppendLine("SESSION USAGE ANALYSIS");
        sb.AppendLine(divider);
        sb.AppendLine();
        sb.AppendLine($"Solution: {result.SolutionPath}");
        sb.AppendLine($"Analyzed: {result.AnalyzedAtUtc:u}");
        sb.AppendLine();

        var anyOperations = false;

        foreach (var project in result.Projects)
        {
            if (project.Diagnostics.Count > 0)
            {
                sb.AppendLine($"[Project: {project.ProjectName}] {project.Diagnostics.Count} diagnostic(s):");
                foreach (var d in project.Diagnostics)
                {
                    sb.AppendLine($"  - {d}");
                }
                sb.AppendLine();
            }

            foreach (var controller in project.Controllers)
            {
                foreach (var action in controller.Actions)
                {
                    if (action.Operations.Count == 0)
                    {
                        continue;
                    }

                    anyOperations = true;

                    foreach (var op in action.Operations)
                    {
                        sb.AppendLine($"Project: {project.ProjectName}");
                        sb.AppendLine($"Controller: {controller.ControllerName}");
                        sb.AppendLine($"Action: {action.ActionName}");
                        sb.AppendLine("File:");
                        sb.AppendLine($"  {op.Location.FilePath}");
                        sb.AppendLine($"Line: {op.Location.Line}");
                        sb.AppendLine($"Operation: {op.OperationType.ToString().ToUpperInvariant()}");
                        if (op.MutationKind is not null)
                        {
                            sb.AppendLine($"Mutation: {op.MutationKind}");
                        }
                        sb.AppendLine($"Expression: {op.Expression}");
                        sb.AppendLine($"Confidence: {op.Confidence}");
                        if (op.Note is not null)
                        {
                            sb.AppendLine($"Note: {op.Note}");
                        }
                        sb.AppendLine($"CanBeReadOnly: {(action.CanPotentiallyUseReadOnlySession ? "YES" : "NO")}");
                        sb.AppendLine(thinDivider);
                    }
                }
            }
        }

        if (!anyOperations)
        {
            sb.AppendLine("No Session usage detected.");
            sb.AppendLine();
        }

        sb.AppendLine(divider);
        sb.AppendLine("ACTION SUMMARY (SessionStateBehavior guidance)");
        sb.AppendLine(divider);
        sb.AppendLine();
        sb.AppendLine($"{"Controller",-30} {"Action",-25} {"Reads",-6} {"Writes",-7} {"CanBeReadOnly",-14}");
        sb.AppendLine(new string('-', 90));

        foreach (var action in result.AllActions.Where(a => a.UsesSession))
        {
            sb.AppendLine($"{action.ControllerName,-30} {action.ActionName,-25} {action.SessionReadCount,-6} {action.SessionWriteCount,-7} {(action.CanPotentiallyUseReadOnlySession ? "YES" : "NO"),-14}");
        }

        sb.AppendLine();
        var totalActions = result.AllActions.Count();
        var sessionActions = result.AllActions.Count(a => a.UsesSession);
        var readOnlyEligible = result.AllActions.Count(a => a.UsesSession && a.CanPotentiallyUseReadOnlySession);
        sb.AppendLine($"Total actions analyzed: {totalActions}");
        sb.AppendLine($"Actions using Session: {sessionActions}");
        sb.AppendLine($"Actions potentially eligible for SessionStateBehavior.ReadOnly: {readOnlyEligible}");
        sb.AppendLine(divider);

        _writer.Write(sb.ToString());
        return Task.CompletedTask;
    }
}
