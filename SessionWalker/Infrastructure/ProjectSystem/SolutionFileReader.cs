using System.Text.RegularExpressions;

namespace SessionWalker.Infrastructure.ProjectSystem;

/// <summary>
/// One C# project entry taken from a Visual Studio solution file.
/// </summary>
public sealed class SolutionProjectEntry
{
    public SolutionProjectEntry(string projectName, string absolutePath, string projectTypeGuid)
    {
        ProjectName = projectName;
        AbsolutePath = absolutePath;
        ProjectTypeGuid = projectTypeGuid;
    }

    public string ProjectName { get; }

    public string AbsolutePath { get; }

    public string ProjectTypeGuid { get; }
}

/// <summary>
/// Minimal replacement for <c>Microsoft.Build.Construction.SolutionFile</c>.
///
/// A .sln file is a flat, line-oriented text format, so reading the handful of
/// <c>Project(...) = ...</c> lines we care about does not require MSBuild to be
/// installed on the machine — which is the whole point of dropping the
/// <c>Microsoft.Build.Locator</c> dependency. Solution folders and non-C#
/// project types are skipped.
/// </summary>
public static class SolutionFileReader
{
    private const string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1AB1AEA671C6}";

    // Project("{TYPE-GUID}") = "Name", "Relative\Path.csproj", "{PROJECT-GUID}"
    private static readonly Regex ProjectLine = new Regex(
        "^Project\\(\"(?<type>\\{[^\"}]*\\})\"\\)\\s*=\\s*\"(?<name>[^\"]*)\"\\s*,\\s*\"(?<path>[^\"]*)\"\\s*,\\s*\"(?<guid>\\{[^\"}]*\\})\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the C# projects referenced by <paramref name="solutionPath"/>.
    /// When a .csproj path is passed instead of a .sln, that single project is
    /// returned so the CLI can analyze a lone project too.
    /// </summary>
    public static IReadOnlyList<SolutionProjectEntry> ReadProjects(string solutionPath)
    {
        solutionPath = Path.GetFullPath(solutionPath);

        if (string.Equals(Path.GetExtension(solutionPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new SolutionProjectEntry(
                    Path.GetFileNameWithoutExtension(solutionPath),
                    solutionPath,
                    string.Empty)
            };
        }

        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        var entries = new List<SolutionProjectEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadLines(solutionPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            var match = ProjectLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var typeGuid = match.Groups["type"].Value;
            if (string.Equals(typeGuid, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            // Only C# projects can be handed to a CSharpCompilation.
            if (!string.Equals(Path.GetExtension(relativePath.Replace('\\', '/')), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string absolutePath;
            try
            {
                absolutePath = PathUtilities.Normalize(solutionDirectory, relativePath);
            }
            catch (Exception)
            {
                continue;
            }

            if (!seen.Add(PathUtilities.ForComparison(absolutePath)))
            {
                continue;
            }

            entries.Add(new SolutionProjectEntry(
                match.Groups["name"].Value,
                absolutePath,
                typeGuid));
        }

        return entries;
    }
}
