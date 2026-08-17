using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;

namespace SessionWalker.Infrastructure.Output;

/// <summary>
/// Writes the full analysis result as JSON containing both the detailed
/// per-operation data and the per-action/controller/project summaries, per
/// design brief section 15 ("projects", "controllers", "actions",
/// "sessionOperations").
/// </summary>
public sealed class JsonOutputWriter : IOutputWriter
{
    public string FormatName => "json";

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task WriteAsync(AnalysisResult result, string destinationPath, CancellationToken cancellationToken)
    {
        var document = new
        {
            solutionPath = result.SolutionPath,
            analyzedAtUtc = result.AnalyzedAtUtc,
            projects = result.Projects.Select(p => new
            {
                projectName = p.ProjectName,
                assemblyName = p.AssemblyName,
                projectFilePath = p.ProjectFilePath,
                diagnostics = p.Diagnostics
            }),
            controllers = result.AllControllers.Select(c => new
            {
                projectName = c.ProjectName,
                @namespace = c.Namespace,
                controllerName = c.ControllerName,
                filePath = c.FilePath,
                anyActionUsesSession = c.AnyActionUsesSession,
                allActionsCanBeReadOnly = c.AllActionsCanBeReadOnly,
                actionCount = c.Actions.Count
            }),
            actions = result.AllActions.Select(a => new
            {
                projectName = a.ProjectName,
                @namespace = a.Namespace,
                controller = a.ControllerName,
                action = a.ActionName,
                filePath = a.Location.FilePath,
                line = a.Location.Line,
                column = a.Location.Column,
                usesSession = a.UsesSession,
                hasSessionRead = a.HasSessionRead,
                hasSessionWrite = a.HasSessionWrite,
                hasSessionMutation = a.HasSessionMutation,
                sessionReadCount = a.SessionReadCount,
                sessionWriteCount = a.SessionWriteCount,
                canPotentiallyUseReadOnlySession = a.CanPotentiallyUseReadOnlySession,
                operations = a.Operations.Select(ToOperationDto)
            }),
            sessionOperations = result.AllSessionOperations.Select(ToOperationDto)
        };

        var json = JsonSerializer.Serialize(document, _options);

        if (string.IsNullOrEmpty(destinationPath))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            File.WriteAllText(destinationPath, json);
        }
    }

    private static object ToOperationDto(SessionOperationResult op) => new
    {
        type = op.OperationType.ToString(),
        mutationKind = op.MutationKind?.ToString(),
        key = op.Key,
        filePath = op.Location.FilePath,
        line = op.Location.Line,
        column = op.Location.Column,
        expression = op.Expression,
        confidence = op.Confidence.ToString(),
        accessPath = op.AccessPath.ToString(),
        note = op.Note,
        project = op.Symbol.ProjectName,
        @namespace = op.Symbol.Namespace,
        @class = op.Symbol.ClassName,
        method = op.Symbol.MethodName,
        controller = op.Symbol.ControllerName,
        action = op.Symbol.ActionName
    };
}
