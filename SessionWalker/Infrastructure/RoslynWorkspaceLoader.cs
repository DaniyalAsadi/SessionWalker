using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace SessionWalker.Infrastructure;


using Microsoft.Build.Construction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
/// <summary>
/// Wraps MSBuildWorkspace so a broken project (missing reference, bad
/// target, unsupported SDK, ...) never takes down the whole analysis run —
/// per design brief section 20, compilation errors must be reported, not
/// fatal. <see cref="LoadResult.Diagnostics"/> collects every
/// WorkspaceFailed event so the CLI can print/log them.
/// </summary>
public sealed class RoslynWorkspaceLoader : IDisposable
{
    private MSBuildWorkspace? _workspace;

    public sealed record LoadResult(
        Solution Solution,
        IReadOnlyList<string> Diagnostics)
    {
        public Solution Solution { get; } = Solution;
        public IReadOnlyList<string> Diagnostics { get; } = Diagnostics;
    }

    public async Task<LoadResult> LoadSolutionAsync(
        string solutionPath,
        bool verbose,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {

        solutionPath = Path.GetFullPath(solutionPath);

        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException("Solution file was not found.", solutionPath);
        }

        var diagnostics = new List<string>();

        var solutionFile = SolutionFile.Parse(solutionPath);

        var projectPaths = solutionFile.ProjectsInOrder
            .Where(p => p.ProjectType != SolutionProjectType.SolutionFolder)
            .Where(p => !string.IsNullOrWhiteSpace(p.AbsolutePath))
            .Where(p => File.Exists(p.AbsolutePath))
            .ToList();

        var properties = new Dictionary<string, string>
        {
            ["Configuration"] = "Debug"
        };

        _workspace = MSBuildWorkspace.Create(properties);

        _workspace.WorkspaceFailed += (_, e) =>
        {
            diagnostics.Add($"[{e.Diagnostic.Kind}] {e.Diagnostic.Message}");
        };

        var msbuildProgress = new Progress<ProjectLoadProgress>(p =>
        {
            if (verbose)
            {
                progress?.Report(
                    $"[{p.Operation}] {p.FilePath} " +
                    $"({p.ElapsedTime.TotalMilliseconds:F0} ms)");
            }
        });

        foreach (var projectEntry in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectPath = Path.GetFullPath(projectEntry.AbsolutePath);

            // OpenProjectAsync may already have loaded this project
            // transitively through ProjectReference.
            var alreadyLoaded = _workspace.CurrentSolution.Projects.Any(p =>
                p.FilePath is not null &&
                string.Equals(
                    Path.GetFullPath(p.FilePath),
                    projectPath,
                    StringComparison.OrdinalIgnoreCase));

            if (alreadyLoaded)
            {
                if (verbose)
                {
                    progress?.Report($"[Already loaded] {projectEntry.ProjectName}");
                }

                continue;
            }

            try
            {
                progress?.Report(
                    $"[Loading] {projectEntry.ProjectName} ({projectPath})");

                await _workspace.OpenProjectAsync(
                    projectPath,
                    msbuildProgress,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    $"[Failed to load] {projectEntry.ProjectName} " +
                    $"({projectPath}): {FormatException(ex)}");
            }
        }

        return new LoadResult(
            _workspace.CurrentSolution,
            diagnostics);
    }

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();

        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            messages.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return string.Join(" --> ", messages);
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
    }
}