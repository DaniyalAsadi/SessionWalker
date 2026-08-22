using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SessionWalker.Infrastructure.ProjectSystem;

namespace SessionWalker.Infrastructure;

/// <summary>
/// Builds a Roslyn <see cref="Solution"/> from a .sln/.csproj without MSBuild.
///
/// The previous implementation used <c>MSBuildWorkspace</c>, which required
/// <c>Microsoft.Build.Locator</c> to discover and load a Visual Studio /
/// Build Tools installation at runtime. That made the tool unusable on any
/// machine without VS installed, and forced the fragile "register MSBuild
/// before touching any MSBuild type" dance. Instead the solution and project
/// files are now read directly (see <see cref="SolutionFileReader"/> and
/// <see cref="ProjectFileReader"/>), sources are parsed by Roslyn itself, and
/// references are resolved best-effort by <see cref="ReferenceResolver"/>.
/// Everything is then projected into an in-memory <see cref="AdhocWorkspace"/>,
/// so the rest of the analyzer keeps working against the exact same
/// Solution/Project/Document/Compilation API as before.
///
/// A broken project (missing reference, unparseable csproj, unsupported
/// project type, ...) never takes down the whole analysis run — per design
/// brief section 20, compilation errors must be reported, not fatal.
/// <see cref="LoadResult.Diagnostics"/> collects every problem so the CLI can
/// print/log them.
/// </summary>
public sealed class RoslynWorkspaceLoader : IDisposable
{
    private AdhocWorkspace? _workspace;

    public sealed record LoadResult(
        Solution Solution,
        IReadOnlyList<string> Diagnostics)
    {
        public Solution Solution { get; } = Solution;
        public IReadOnlyList<string> Diagnostics { get; } = Diagnostics;
    }

    public Task<LoadResult> LoadSolutionAsync(
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
        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();

        IReadOnlyList<SolutionProjectEntry> projectEntries;
        try
        {
            projectEntries = SolutionFileReader.ReadProjects(solutionPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to read solution file '{solutionPath}': {ex.Message}", ex);
        }

        if (projectEntries.Count == 0)
        {
            diagnostics.Add($"[No projects] No C# projects were found in '{solutionPath}'.");
        }

        var referenceResolver = new ReferenceResolver(solutionDirectory);

        // Read every project file first so ProjectReference edges can be wired
        // up by ProjectId once all of them are known.
        var projectInfos = new List<ProjectFileInfo>();
        var projectIdsByPath = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        var infosByPath = new Dictionary<string, ProjectFileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in projectEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(entry.AbsolutePath))
            {
                diagnostics.Add(
                    $"[Missing project] {entry.ProjectName} ({entry.AbsolutePath}) " +
                    "is listed in the solution but does not exist on disk.");
                continue;
            }

            var key = PathUtilities.ForComparison(entry.AbsolutePath);
            if (infosByPath.ContainsKey(key))
            {
                continue;
            }

            progress?.Report($"[Loading] {entry.ProjectName} ({entry.AbsolutePath})");

            ProjectFileInfo info;
            try
            {
                info = ProjectFileReader.Read(entry.AbsolutePath, configuration: "Debug");
            }
            catch (Exception ex)
            {
                diagnostics.Add(
                    $"[Failed to load] {entry.ProjectName} ({entry.AbsolutePath}): {FormatException(ex)}");
                continue;
            }

            // The solution file's display name wins, matching MSBuildWorkspace.
            if (!string.IsNullOrWhiteSpace(entry.ProjectName))
            {
                info.Name = entry.ProjectName;
            }

            diagnostics.AddRange(info.Diagnostics);

            projectInfos.Add(info);
            infosByPath[key] = info;
            projectIdsByPath[key] = ProjectId.CreateNewId(info.Name);
        }

        // Projects referenced only transitively (via ProjectReference) are not
        // listed in the .sln in some repositories; pull them in so
        // interprocedural tracing can still cross project boundaries.
        var pending = new Queue<string>(projectInfos.SelectMany(p => p.ProjectReferences));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var referencedPath = pending.Dequeue();
            var key = PathUtilities.ForComparison(referencedPath);

            if (infosByPath.ContainsKey(key))
            {
                continue;
            }

            if (!File.Exists(referencedPath))
            {
                diagnostics.Add($"[Missing project reference] {referencedPath} was not found.");
                continue;
            }

            if (!string.Equals(Path.GetExtension(referencedPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ProjectFileInfo info;
            try
            {
                info = ProjectFileReader.Read(referencedPath, configuration: "Debug");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"[Failed to load] {referencedPath}: {FormatException(ex)}");
                continue;
            }

            if (verbose)
            {
                progress?.Report($"[Loading transitively] {info.Name} ({referencedPath})");
            }

            diagnostics.AddRange(info.Diagnostics);

            projectInfos.Add(info);
            infosByPath[key] = info;
            projectIdsByPath[key] = ProjectId.CreateNewId(info.Name);

            foreach (var nested in info.ProjectReferences)
            {
                pending.Enqueue(nested);
            }
        }

        var projectInfoList = new List<ProjectInfo>();

        foreach (var info in projectInfos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var projectId = projectIdsByPath[PathUtilities.ForComparison(info.FilePath)];

            var documents = new List<DocumentInfo>();
            var parseOptions = CreateParseOptions(info);

            foreach (var file in info.CompileItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(file))
                {
                    diagnostics.Add($"[Missing source] {file} (referenced by '{info.Name}')");
                    continue;
                }

                SourceText sourceText;
                try
                {
                    using var stream = File.OpenRead(file);
                    sourceText = SourceText.From(stream, canBeEmbedded: false);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"[Unreadable source] {file}: {ex.Message}");
                    continue;
                }

                documents.Add(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId, file),
                    name: Path.GetFileName(file),
                    folders: GetFolders(info.Directory, file),
                    sourceCodeKind: SourceCodeKind.Regular,
                    loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), file)),
                    filePath: file));
            }

            if (info.ImplicitUsings)
            {
                // The SDK normally generates an ImplicitGlobalUsings.g.cs into
                // obj\; that file is not guaranteed to exist (the project may
                // never have been built), so synthesize an equivalent one.
                var implicitUsings = BuildImplicitGlobalUsings(info);
                var virtualPath = Path.Combine(info.Directory, "SessionWalker.ImplicitGlobalUsings.g.cs");

                documents.Add(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId, virtualPath),
                    name: "SessionWalker.ImplicitGlobalUsings.g.cs",
                    sourceCodeKind: SourceCodeKind.Regular,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(implicitUsings), VersionStamp.Create(), virtualPath)),
                    filePath: virtualPath));
            }

            var metadataReferences = referenceResolver.Resolve(info, diagnostics);

            var projectReferences = new List<ProjectReference>();
            foreach (var referencedPath in info.ProjectReferences)
            {
                if (projectIdsByPath.TryGetValue(PathUtilities.ForComparison(referencedPath), out var referencedId) &&
                    referencedId != projectId)
                {
                    projectReferences.Add(new ProjectReference(referencedId));
                }
            }

            projectInfoList.Add(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: info.Name,
                assemblyName: info.AssemblyName,
                language: LanguageNames.CSharp,
                filePath: info.FilePath,
                outputFilePath: null,
                compilationOptions: CreateCompilationOptions(info),
                parseOptions: parseOptions,
                documents: documents,
                projectReferences: projectReferences,
                metadataReferences: metadataReferences));

            stopwatch.Stop();

            if (verbose)
            {
                progress?.Report(
                    $"[Loaded] {info.FilePath} — {documents.Count} document(s), " +
                    $"{metadataReferences.Count} reference(s) " +
                    $"({stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
            }
        }

        _workspace = new AdhocWorkspace();

        var solution = _workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(Path.GetFileNameWithoutExtension(solutionPath)),
            VersionStamp.Create(),
            filePath: solutionPath,
            projects: projectInfoList));

        return Task.FromResult(new LoadResult(solution, diagnostics));
    }

    private static string BuildImplicitGlobalUsings(ProjectFileInfo info)
    {
        var namespaces = new List<string>
        {
            "System",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Net.Http",
            "System.Threading",
            "System.Threading.Tasks"
        };

        _ = info;

        return string.Join(
            Environment.NewLine,
            namespaces.Select(ns => $"global using global::{ns};"));
    }

    private static CSharpParseOptions CreateParseOptions(ProjectFileInfo info)
    {
        var languageVersion = LanguageVersion.Latest;

        if (!string.IsNullOrWhiteSpace(info.LangVersion) &&
            LanguageVersionFacts.TryParse(info.LangVersion, out var parsed))
        {
            languageVersion = parsed;
        }

        var preprocessorSymbols = info.DefineConstants.ToList();
        if (preprocessorSymbols.Count == 0)
        {
            preprocessorSymbols.Add("DEBUG");
            preprocessorSymbols.Add("TRACE");
        }

        return new CSharpParseOptions(
            languageVersion: languageVersion,
            documentationMode: DocumentationMode.Parse,
            kind: SourceCodeKind.Regular,
            preprocessorSymbols: preprocessorSymbols);
    }

    private static CSharpCompilationOptions CreateCompilationOptions(ProjectFileInfo info)
    {
        var outputKind = info.OutputType.Trim().ToLowerInvariant() switch
        {
            "exe" => OutputKind.ConsoleApplication,
            "winexe" => OutputKind.WindowsApplication,
            _ => OutputKind.DynamicallyLinkedLibrary
        };

        return new CSharpCompilationOptions(
            outputKind,
            allowUnsafe: info.AllowUnsafe,
            nullableContextOptions: info.NullableEnabled
                ? NullableContextOptions.Enable
                : NullableContextOptions.Disable,
            // Analysis only needs binding, never emit, so missing entry points
            // or unresolved references must not be escalated.
            reportSuppressedDiagnostics: false,
            assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default);
    }

    private static IReadOnlyList<string> GetFolders(string projectDirectory, string filePath)
    {
        var relative = PathUtilities.MakeRelative(projectDirectory, filePath);
        if (relative is null)
        {
            return Array.Empty<string>();
        }

        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length <= 1
            ? Array.Empty<string>()
            : segments.Take(segments.Length - 1).ToArray();
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
