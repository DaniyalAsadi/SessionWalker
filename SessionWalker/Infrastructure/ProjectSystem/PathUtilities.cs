namespace SessionWalker.Infrastructure.ProjectSystem;

/// <summary>
/// Small path helpers shared by the hand-rolled solution/project readers.
///
/// MSBuild project files always use Windows-style back slashes, even when the
/// file is read on another operating system, so every path coming out of a
/// .sln/.csproj has to be normalized before it is handed to <see cref="Path"/>.
/// </summary>
internal static class PathUtilities
{
    /// <summary>
    /// Converts an MSBuild-authored (back slash) relative or absolute path into
    /// a rooted, normalized path for the current platform.
    /// </summary>
    public static string Normalize(string baseDirectory, string path)
    {
        var normalized = path.Trim()
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (!Path.IsPathRooted(normalized))
        {
            normalized = Path.Combine(baseDirectory, normalized);
        }

        return Path.GetFullPath(normalized);
    }

    /// <summary>
    /// Normalizes a path only far enough to compare two paths for equality.
    /// </summary>
    public static string ForComparison(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path;
        }
    }

    public static bool AreSame(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            ForComparison(left),
            ForComparison(right),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureTrailingSeparator(string directory)
    {
        if (directory.Length == 0)
        {
            return directory;
        }

        var last = directory[directory.Length - 1];
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
        {
            return directory;
        }

        return directory + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Expands an MSBuild item <c>Include</c> pattern that may contain
    /// <c>*</c>, <c>?</c> or a recursive <c>**</c> segment.
    /// </summary>
    public static IReadOnlyList<string> ExpandGlob(string baseDirectory, string pattern)
    {
        var empty = Array.Empty<string>();

        var normalized = pattern.Trim()
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (normalized.Length == 0)
        {
            return empty;
        }

        if (normalized.IndexOf('*') < 0 && normalized.IndexOf('?') < 0)
        {
            string single;
            try
            {
                single = Normalize(baseDirectory, normalized);
            }
            catch (Exception)
            {
                return empty;
            }

            return File.Exists(single) ? new[] { single } : empty;
        }

        var recursiveMarker = "**" + Path.DirectorySeparatorChar;
        var recursiveIndex = normalized.IndexOf(recursiveMarker, StringComparison.Ordinal);

        string searchRoot;
        string filePattern;
        SearchOption searchOption;

        if (recursiveIndex >= 0)
        {
            searchRoot = normalized.Substring(0, recursiveIndex);
            filePattern = normalized.Substring(recursiveIndex + recursiveMarker.Length);
            searchOption = SearchOption.AllDirectories;

            // A pattern such as "src\**\sub\*.cs" cannot be expressed with a
            // single Directory.EnumerateFiles call; fall back to matching only
            // on the final file name and filtering afterwards.
            var lastSeparator = filePattern.LastIndexOf(Path.DirectorySeparatorChar);
            if (lastSeparator >= 0)
            {
                filePattern = filePattern.Substring(lastSeparator + 1);
            }
        }
        else
        {
            var lastSeparator = normalized.LastIndexOf(Path.DirectorySeparatorChar);
            searchRoot = lastSeparator >= 0 ? normalized.Substring(0, lastSeparator) : string.Empty;
            filePattern = lastSeparator >= 0 ? normalized.Substring(lastSeparator + 1) : normalized;
            searchOption = SearchOption.TopDirectoryOnly;
        }

        if (filePattern.Length == 0)
        {
            return empty;
        }

        string rootDirectory;
        try
        {
            rootDirectory = searchRoot.Length == 0
                ? Path.GetFullPath(baseDirectory)
                : Normalize(baseDirectory, searchRoot);
        }
        catch (Exception)
        {
            return empty;
        }

        if (!Directory.Exists(rootDirectory))
        {
            return empty;
        }

        try
        {
            return Directory.EnumerateFiles(rootDirectory, filePattern, searchOption)
                .Select(Path.GetFullPath)
                .ToList();
        }
        catch (Exception)
        {
            return empty;
        }
    }

    /// <summary>
    /// True when the file lives under an <c>obj</c> or <c>bin</c> directory,
    /// which SDK-style default globs exclude.
    /// </summary>
    public static bool IsUnderIntermediateOutput(string projectDirectory, string filePath)
    {
        var relative = MakeRelative(projectDirectory, filePath);
        if (relative is null)
        {
            return false;
        }

        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // Only the leading segments matter; a folder literally called "bin"
        // deep inside a source tree is unusual but still excluded by MSBuild.
        return segments.Any(s =>
            string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase));
    }

    public static string? MakeRelative(string baseDirectory, string path)
    {
        var basePath = EnsureTrailingSeparator(ForComparison(baseDirectory));
        var fullPath = ForComparison(path);

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(basePath.Length);
        }

        return null;
    }
}
