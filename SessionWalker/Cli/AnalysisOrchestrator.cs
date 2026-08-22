using Microsoft.CodeAnalysis;
using SessionWalker.Analyzer;
using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;
using SessionWalker.Infrastructure;

namespace SessionWalker.Cli;

/// <summary>
/// Ties workspace loading, the registered <see cref="ICodeAnalyzer"/>
/// pipeline, and result aggregation together. New analyzers are added by
/// listing them in <see cref="CreateAnalyzers"/> — nothing else in the CLI
/// needs to change (design brief section 19/24).
/// </summary>
public sealed class AnalysisOrchestrator
{
    public static IReadOnlyList<ICodeAnalyzer> CreateAnalyzers() => new ICodeAnalyzer[]
    {
        new SessionUsageAnalyzer()
        // Future: new ViewBagAnalyzer(), new TempDataAnalyzer(), new HttpContextAnalyzer(), ...
    };

    public async Task<AnalysisResult> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        using var loader = new RoslynWorkspaceLoader();

        var progress = new Progress<string>(msg =>
        {
            if (options.Verbose)
            {
                Console.Error.WriteLine(msg);
            }
        });

        var loadResult = await loader.LoadSolutionAsync(options.SolutionPath!, options.Verbose, progress, cancellationToken).ConfigureAwait(false);

        var solution = loadResult.Solution;
        var semanticModelCache = new SemanticModelCache();
        var analyzers = CreateAnalyzers();

        var projectResults = new List<ProjectAnalysisResult>();

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            if (options.ProjectFilter is not null &&
                !string.Equals(project.Name, options.ProjectFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var diagnostics = new List<string>(loadResult.Diagnostics);

            Compilation? compilation;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Failed to compile project '{project.Name}': {ex.Message}");
                projectResults.Add(new ProjectAnalysisResult(project.Name, project.AssemblyName, project.FilePath ?? string.Empty, Array.Empty<ControllerAnalysisResult>(), Array.Empty<SessionOperationResult>(), diagnostics));
                continue;
            }

            if (compilation is null)
            {
                diagnostics.Add($"Project '{project.Name}' produced no compilation (unsupported project type or load failure).");
                projectResults.Add(new ProjectAnalysisResult(project.Name, project.AssemblyName, project.FilePath ?? string.Empty, Array.Empty<ControllerAnalysisResult>(), Array.Empty<SessionOperationResult>(), diagnostics));
                continue;
            }

            var compileErrors = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(25)
                .Select(d => d.ToString());
            diagnostics.AddRange(compileErrors);

            var context = new AnalysisContext
            {
                Solution = solution,
                Project = project,
                Compilation = compilation,
                SemanticModelCache = semanticModelCache,
                Verbose = options.Verbose
            };

            var allOperations = new List<SessionOperationResult>();
            var allControllers = new List<ControllerAnalysisResult>();

            foreach (var analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AnalyzerResult analyzerResult;
                try
                {
                    analyzerResult = await analyzer.AnalyzeAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"Analyzer '{analyzer.Name}' failed on project '{project.Name}': {ex.Message}");
                    continue;
                }

                diagnostics.AddRange(analyzerResult.Diagnostics);

                if (analyzerResult.Payload is SessionAnalyzerResult sessionResult)
                {
                    allOperations.AddRange(sessionResult.AllOperations);
                    allControllers.AddRange(sessionResult.Controllers);
                }
            }

            projectResults.Add(new ProjectAnalysisResult(
                project.Name,
                project.AssemblyName,
                project.FilePath ?? string.Empty,
                allControllers,
                allOperations,
                diagnostics));
        }

        var result = new AnalysisResult(options.SolutionPath!, DateTimeOffset.UtcNow, projectResults);
        return ApplyFilters(result, options);
    }

    private static AnalysisResult ApplyFilters(AnalysisResult result, CliOptions options)
    {
        if (!options.SessionOnly && !options.SessionWriteOnly && !options.ControllerOnly)
        {
            return result;
        }

        var filteredProjects = result.Projects.Select(project =>
        {
            var filteredOperations = project.AllSessionOperations.AsEnumerable();
            if (options.SessionWriteOnly)
            {
                filteredOperations = filteredOperations.Where(o => o.IsWrite);
            }

            var filteredControllers = project.Controllers
                .Select(controller =>
                {
                    var actions = controller.Actions
                        .Select(action =>
                        {
                            var ops = action.Operations.AsEnumerable();
                            if (options.SessionWriteOnly)
                            {
                                ops = ops.Where(o => o.IsWrite);
                            }
                            return action with { Operations = ops.ToList() };
                        })
                        .Where(a => a.UsesSession || (!options.SessionOnly && !options.SessionWriteOnly))
                        .ToList();

                    return controller with { Actions = actions };
                })
                .Where(c => c.Actions.Count > 0)
                .ToList();

            return project with
            {
                AllSessionOperations = filteredOperations.ToList(),
                Controllers = filteredControllers
            };
        }).ToList();

        return result with { Projects = filteredProjects };
    }
}
