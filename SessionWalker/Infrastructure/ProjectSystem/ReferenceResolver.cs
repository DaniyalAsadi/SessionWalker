using Microsoft.CodeAnalysis;

namespace SessionWalker.Infrastructure.ProjectSystem;

/// <summary>
/// Best-effort resolution of a project's assembly references to files on disk,
/// without asking MSBuild to run <c>ResolveAssemblyReference</c>.
///
/// Sources, in priority order:
///   1. an explicit <c>HintPath</c> in the .csproj;
///   2. the NuGet packages folder (<c>packages\Id.Version\lib\...</c> next to
///      the solution for packages.config projects, and the global
///      <c>~\.nuget\packages\id\version\lib\...</c> folder for PackageReference);
///   3. the .NET Framework reference assemblies directory matching the
///      project's <c>TargetFrameworkVersion</c>;
///   4. the runtime directory of the process as a final fallback.
///
/// Anything that cannot be resolved is reported as a diagnostic rather than
/// failing the run: Roslyn still produces a usable semantic model for the parts
/// of the code whose types did resolve, and the design brief requires
/// compilation errors to be surfaced, not fatal.
/// </summary>
public sealed class ReferenceResolver
{
    private static readonly string[] LibFolderPreference =
    {
        "net48", "net472", "net471", "net47", "net462", "net461", "net46",
        "net452", "net451", "net45", "net40", "net35", "net20",
        "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.5",
        "netstandard1.4", "netstandard1.3", "netstandard1.2", "netstandard1.1",
        "netstandard1.0"
    };

    private readonly string _solutionDirectory;
    private readonly Dictionary<string, MetadataReference> _cache =
        new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

    public ReferenceResolver(string solutionDirectory)
    {
        _solutionDirectory = solutionDirectory;
    }

    /// <summary>
    /// Resolves every reference declared by <paramref name="project"/>.
    /// </summary>
    public IReadOnlyList<MetadataReference> Resolve(ProjectFileInfo project, List<string> diagnostics)
    {
        var results = new List<MetadataReference>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworkDirectories = GetFrameworkDirectories(project.TargetFramework);
        var packageDirectories = GetPackageDirectories(project);

        void Add(string path)
        {
            var key = PathUtilities.ForComparison(path);
            if (!added.Add(key))
            {
                return;
            }

            var reference = CreateReference(path, diagnostics);
            if (reference is not null)
            {
                results.Add(reference);
            }
        }

        // mscorlib and friends are implicit in MSBuild; add them up front so a
        // project that only lists <Reference Include="System" /> still binds.
        foreach (var implicitAssembly in GetImplicitFrameworkAssemblies(project))
        {
            var resolved = FindInDirectories(implicitAssembly, frameworkDirectories);
            if (resolved is not null)
            {
                Add(resolved);
            }
        }

        foreach (var reference in project.References)
        {
            var simpleName = reference.SimpleName;
            if (simpleName.Length == 0)
            {
                continue;
            }

            if (reference.HintPath is not null && File.Exists(reference.HintPath))
            {
                Add(reference.HintPath);
                continue;
            }

            var fromPackages = FindInDirectories(simpleName, packageDirectories);
            if (fromPackages is not null)
            {
                Add(fromPackages);
                continue;
            }

            var fromFramework = FindInDirectories(simpleName, frameworkDirectories);
            if (fromFramework is not null)
            {
                Add(fromFramework);
                continue;
            }

            diagnostics.Add(
                $"[Unresolved reference] '{simpleName}' in project '{project.Name}' " +
                "could not be located; types from it will be reported as errors.");
        }

        // PackageReference assemblies are never listed as <Reference> items, so
        // pull their lib\ assemblies in directly.
        foreach (var package in project.PackageReferences)
        {
            foreach (var assembly in ResolvePackageAssemblies(package.Key, package.Value, project.TargetFramework))
            {
                Add(assembly);
            }
        }

        if (results.Count == 0)
        {
            diagnostics.Add(
                $"[No references resolved] Project '{project.Name}' was compiled without any " +
                "assembly references; semantic results will be limited.");
        }

        return results;
    }

    private MetadataReference? CreateReference(string path, List<string> diagnostics)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            // Reading through a stream keeps the file unlocked, which matters
            // when the analyzed solution is open in Visual Studio at the time.
            using var stream = File.OpenRead(path);
            var reference = MetadataReference.CreateFromStream(
                stream,
                filePath: path);

            _cache[path] = reference;
            return reference;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"[Unreadable reference] {path}: {ex.Message}");
            return null;
        }
    }

    private static string? FindInDirectories(string assemblySimpleName, IReadOnlyList<string> directories)
    {
        var fileName = assemblySimpleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? assemblySimpleName
            : assemblySimpleName + ".dll";

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetPackageDirectories(ProjectFileInfo project)
    {
        var directories = new List<string>();

        // packages.config layout: <solution>\packages\Id.Version\lib\<tfm>\*.dll
        var solutionPackages = Path.Combine(_solutionDirectory, "packages");
        if (Directory.Exists(solutionPackages))
        {
            foreach (var packageDirectory in SafeEnumerateDirectories(solutionPackages))
            {
                var lib = Path.Combine(packageDirectory, "lib");
                if (!Directory.Exists(lib))
                {
                    continue;
                }

                var best = SelectBestLibFolder(lib, project.TargetFramework);
                if (best is not null)
                {
                    directories.Add(best);
                }
            }
        }

        return directories;
    }

    private IReadOnlyList<string> ResolvePackageAssemblies(
        string packageId,
        string version,
        string targetFramework)
    {
        if (packageId.Length == 0)
        {
            return Array.Empty<string>();
        }

        foreach (var root in GetNuGetRoots())
        {
            var packageRoot = Path.Combine(root, packageId.ToLowerInvariant());
            if (!Directory.Exists(packageRoot))
            {
                packageRoot = Path.Combine(root, packageId);
                if (!Directory.Exists(packageRoot))
                {
                    continue;
                }
            }

            var versionDirectory = version.Length > 0
                ? Path.Combine(packageRoot, version)
                : SafeEnumerateDirectories(packageRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).LastOrDefault();

            if (versionDirectory is null || !Directory.Exists(versionDirectory))
            {
                continue;
            }

            var lib = Path.Combine(versionDirectory, "lib");
            var folder = Directory.Exists(lib) ? SelectBestLibFolder(lib, targetFramework) : null;

            if (folder is null)
            {
                var referenceFolder = Path.Combine(versionDirectory, "ref");
                folder = Directory.Exists(referenceFolder)
                    ? SelectBestLibFolder(referenceFolder, targetFramework)
                    : null;
            }

            if (folder is null)
            {
                continue;
            }

            var assemblies = SafeEnumerateFiles(folder, "*.dll").ToList();
            if (assemblies.Count > 0)
            {
                return assemblies;
            }
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> GetNuGetRoots()
    {
        var roots = new List<string>();

        void AddIfExists(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory) &&
                !roots.Any(r => PathUtilities.AreSame(r, directory!)))
            {
                roots.Add(directory!);
            }
        }

        AddIfExists(Environment.GetEnvironmentVariable("NUGET_PACKAGES"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userProfile.Length > 0)
        {
            AddIfExists(Path.Combine(userProfile, ".nuget", "packages"));
        }

        AddIfExists(Path.Combine(_solutionDirectory, "packages"));

        return roots;
    }

    private static string? SelectBestLibFolder(string libRoot, string targetFramework)
    {
        var candidates = SafeEnumerateDirectories(libRoot)
            .ToDictionary(d => Path.GetFileName(d) ?? string.Empty, d => d, StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0)
        {
            // Some old packages put assemblies straight in lib\.
            return SafeEnumerateFiles(libRoot, "*.dll").Any() ? libRoot : null;
        }

        var preferred = NormalizeTargetFrameworkMoniker(targetFramework);
        if (preferred is not null && candidates.TryGetValue(preferred, out var exact))
        {
            return exact;
        }

        foreach (var moniker in LibFolderPreference)
        {
            if (candidates.TryGetValue(moniker, out var match))
            {
                return match;
            }
        }

        return candidates.Values.FirstOrDefault();
    }

    /// <summary>Turns "v4.7.2" into "net472"; passes SDK-style monikers through.</summary>
    private static string? NormalizeTargetFrameworkMoniker(string targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return null;
        }

        var value = targetFramework.Trim();

        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            return "net" + value.Substring(1).Replace(".", string.Empty);
        }

        return value;
    }

    private static IReadOnlyList<string> GetFrameworkDirectories(string targetFramework)
    {
        var directories = new List<string>();

        void AddIfExists(string? directory)
        {
            if (!string.IsNullOrEmpty(directory) &&
                Directory.Exists(directory) &&
                !directories.Any(d => PathUtilities.AreSame(d, directory!)))
            {
                directories.Add(directory!);
            }
        }

        var version = targetFramework.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? targetFramework
            : null;

        // Reference assemblies normally live under Program Files (x86), but
        // check both roots so 32/64-bit hosts and custom layouts both work.
        var programFilesRoots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles")
        };

        foreach (var programFiles in programFilesRoots)
        {
            if (string.IsNullOrEmpty(programFiles))
            {
                continue;
            }

            var referenceAssemblyRoot = Path.Combine(
                programFiles!,
                "Reference Assemblies",
                "Microsoft",
                "Framework",
                ".NETFramework");

            // Prefer the set matching the project's TargetFrameworkVersion.
            if (version is not null)
            {
                AddIfExists(Path.Combine(referenceAssemblyRoot, version));
            }

            // Then fall back to the newest installed set.
            foreach (var directory in SafeEnumerateDirectories(referenceAssemblyRoot)
                         .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                AddIfExists(directory);
            }
        }

        // Mono / non-Windows hosts keep framework assemblies elsewhere.
        if (Environment.OSVersion.Platform is PlatformID.Unix or PlatformID.MacOSX)
        {
            foreach (var monoRoot in new[] { "/usr/lib/mono", "/usr/local/lib/mono" })
            {
                if (!Directory.Exists(monoRoot))
                {
                    continue;
                }

                foreach (var directory in SafeEnumerateDirectories(monoRoot)
                             .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    AddIfExists(directory);
                }
            }
        }

        AddIfExists(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

        return directories;
    }

    private static IEnumerable<string> GetImplicitFrameworkAssemblies(ProjectFileInfo project)
    {
        yield return "mscorlib";
        yield return "System";
        yield return "System.Core";

        if (!project.IsSdkStyle)
        {
            yield break;
        }

        yield return "netstandard";
        yield return "System.Runtime";
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).ToList()
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, pattern).ToList()
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
