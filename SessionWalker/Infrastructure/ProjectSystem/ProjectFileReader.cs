using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SessionWalker.Infrastructure.ProjectSystem;

/// <summary>
/// An assembly reference declared by a project, before it has been resolved to
/// a file on disk.
/// </summary>
public sealed class AssemblyReferenceInfo
{
    public AssemblyReferenceInfo(string include, string? hintPath)
    {
        Include = include;
        HintPath = hintPath;
    }

    /// <summary>The raw <c>Include</c>, e.g. "System.Web" or a full assembly name with version/culture/token.</summary>
    public string Include { get; }

    /// <summary>Simple assembly name ("System.Web") with any strong-name qualifiers stripped.</summary>
    public string SimpleName
    {
        get
        {
            var comma = Include.IndexOf(',');
            return (comma >= 0 ? Include.Substring(0, comma) : Include).Trim();
        }
    }

    /// <summary>Absolute path from a <c>HintPath</c> element, when the project supplied one.</summary>
    public string? HintPath { get; }
}

/// <summary>
/// Everything read out of a single .csproj that the analyzer needs to build a
/// <c>CSharpCompilation</c> without MSBuild.
/// </summary>
public sealed class ProjectFileInfo
{
    public string FilePath { get; set; } = string.Empty;

    public string Directory { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AssemblyName { get; set; } = string.Empty;

    public string RootNamespace { get; set; } = string.Empty;

    public bool IsSdkStyle { get; set; }

    /// <summary>e.g. "v4.7.2" for legacy projects, "net8.0" for SDK-style ones.</summary>
    public string TargetFramework { get; set; } = string.Empty;

    public string OutputType { get; set; } = "Library";

    public string LangVersion { get; set; } = string.Empty;

    public bool AllowUnsafe { get; set; }

    public bool NullableEnabled { get; set; }

    public bool ImplicitUsings { get; set; }

    public List<string> DefineConstants { get; } = new List<string>();

    public List<string> CompileItems { get; } = new List<string>();

    public List<AssemblyReferenceInfo> References { get; } = new List<AssemblyReferenceInfo>();

    public List<string> ProjectReferences { get; } = new List<string>();

    /// <summary>Package id/version pairs from PackageReference items and packages.config.</summary>
    public List<KeyValuePair<string, string>> PackageReferences { get; } = new List<KeyValuePair<string, string>>();

    public List<string> Diagnostics { get; } = new List<string>();
}

/// <summary>
/// Hand-rolled .csproj reader replacing <c>Microsoft.Build.Evaluation.Project</c>.
///
/// This is deliberately a *reader*, not an evaluator: it does not run targets,
/// import .props/.targets files, or resolve arbitrary MSBuild expressions. It
/// understands the subset that matters for semantic analysis — source files,
/// assembly/project references, preprocessor symbols and language version —
/// which is enough to produce a compilation that Roslyn can bind against, and
/// it works on any machine without Visual Studio or MSBuild installed.
///
/// Property values may contain simple <c>$(Name)</c> references; those are
/// substituted from properties already seen in the same file (plus a few
/// well-known ones such as <c>$(MSBuildProjectDirectory)</c>). Anything else is
/// left as-is rather than guessed at.
/// </summary>
public static class ProjectFileReader
{
    private static readonly Regex PropertyReference = new Regex(
        @"\$\((?<name>[A-Za-z_][A-Za-z0-9_.]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] SdkDefaultCompileGlobs = { "**" + Path.DirectorySeparatorChar + "*.cs" };

    public static ProjectFileInfo Read(string projectPath, string configuration)
    {
        projectPath = Path.GetFullPath(projectPath);

        var info = new ProjectFileInfo
        {
            FilePath = projectPath,
            Directory = Path.GetDirectoryName(projectPath) ?? System.IO.Directory.GetCurrentDirectory(),
            Name = Path.GetFileNameWithoutExtension(projectPath)
        };

        XDocument document;
        using (var stream = File.OpenRead(projectPath))
        {
            document = XDocument.Load(stream, LoadOptions.None);
        }

        var root = document.Root;
        if (root is null)
        {
            info.Diagnostics.Add($"Project '{info.Name}' is empty or malformed.");
            return info;
        }

        info.IsSdkStyle = root.Attribute("Sdk") is not null ||
                          root.Elements().Any(e => LocalName(e) == "Sdk");

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectDirectory"] = info.Directory,
            ["MSBuildProjectName"] = info.Name,
            ["MSBuildProjectFullPath"] = projectPath,
            ["MSBuildThisFileDirectory"] = PathUtilities.EnsureTrailingSeparator(info.Directory),
            ["Configuration"] = configuration,
            ["Platform"] = "AnyCPU"
        };

        ReadProperties(root, properties, configuration);
        ApplyProperties(info, properties);
        ReadItems(root, info, properties, configuration);

        if (info.CompileItems.Count == 0 && info.IsSdkStyle)
        {
            AddSdkDefaultCompileItems(info);
        }

        ReadPackagesConfig(info);

        info.CompileItems.Sort(StringComparer.OrdinalIgnoreCase);

        return info;
    }

    private static string LocalName(XElement element) => element.Name.LocalName;

    /// <summary>
    /// Property/item groups nested in a &lt;Target&gt; only take effect while
    /// that target runs, so they must not contribute to the static view of the
    /// project.
    /// </summary>
    private static bool IsInsideTarget(XElement element)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (LocalName(parent) == "Target")
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadProperties(
        XElement root,
        Dictionary<string, string> properties,
        string configuration)
    {
        foreach (var group in root.Descendants().Where(e => LocalName(e) == "PropertyGroup" && !IsInsideTarget(e)))
        {
            if (!ConditionApplies(group.Attribute("Condition")?.Value, properties, configuration))
            {
                continue;
            }

            foreach (var property in group.Elements())
            {
                if (!ConditionApplies(property.Attribute("Condition")?.Value, properties, configuration))
                {
                    continue;
                }

                var name = LocalName(property);
                var value = Expand(property.Value, properties);

                // DefineConstants is usually written as "$(DefineConstants);TRACE",
                // so it accumulates rather than replaces.
                properties[name] = value;
            }
        }
    }

    private static void ApplyProperties(ProjectFileInfo info, IDictionary<string, string> properties)
    {
        info.AssemblyName = Get(properties, "AssemblyName", info.Name);
        info.RootNamespace = Get(properties, "RootNamespace", info.AssemblyName);
        info.OutputType = Get(properties, "OutputType", "Library");
        info.LangVersion = Get(properties, "LangVersion", string.Empty);
        info.AllowUnsafe = IsTrue(Get(properties, "AllowUnsafeBlocks", "false"));
        info.NullableEnabled = Get(properties, "Nullable", string.Empty)
            .Trim()
            .Equals("enable", StringComparison.OrdinalIgnoreCase);
        info.ImplicitUsings = IsTrue(Get(properties, "ImplicitUsings", "false")) ||
                              Get(properties, "ImplicitUsings", string.Empty)
                                  .Trim()
                                  .Equals("enable", StringComparison.OrdinalIgnoreCase);

        var targetFramework = Get(properties, "TargetFramework", string.Empty);
        if (targetFramework.Length == 0)
        {
            targetFramework = Get(properties, "TargetFrameworks", string.Empty)
                .Split(';')
                .FirstOrDefault(s => s.Trim().Length > 0)
                ?.Trim() ?? string.Empty;
        }

        if (targetFramework.Length == 0)
        {
            targetFramework = Get(properties, "TargetFrameworkVersion", string.Empty);
        }

        info.TargetFramework = targetFramework;

        var defines = Get(properties, "DefineConstants", string.Empty);
        foreach (var symbol in defines.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = symbol.Trim();
            if (trimmed.Length > 0 && !info.DefineConstants.Contains(trimmed))
            {
                info.DefineConstants.Add(trimmed);
            }
        }
    }

    private static void ReadItems(
        XElement root,
        ProjectFileInfo info,
        IDictionary<string, string> properties,
        string configuration)
    {
        var compileSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedCompile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in root.Descendants().Where(e => LocalName(e) == "ItemGroup" && !IsInsideTarget(e)))
        {
            if (!ConditionApplies(group.Attribute("Condition")?.Value, properties, configuration))
            {
                continue;
            }

            foreach (var item in group.Elements())
            {
                if (!ConditionApplies(item.Attribute("Condition")?.Value, properties, configuration))
                {
                    continue;
                }

                var itemType = LocalName(item);
                var include = Expand(item.Attribute("Include")?.Value ?? string.Empty, properties);
                var remove = Expand(item.Attribute("Remove")?.Value ?? string.Empty, properties);

                switch (itemType)
                {
                    case "Compile":
                        ReadCompileItem(info, include, remove, compileSeen, removedCompile);
                        break;

                    case "Reference":
                        ReadReferenceItem(info, item, include, properties);
                        break;

                    case "ProjectReference":
                        ReadProjectReferenceItem(info, include);
                        break;

                    case "PackageReference":
                        ReadPackageReferenceItem(info, item, include, properties);
                        break;
                }
            }
        }

        if (removedCompile.Count > 0)
        {
            info.CompileItems.RemoveAll(f => removedCompile.Contains(PathUtilities.ForComparison(f)));
        }
    }

    private static void ReadCompileItem(
        ProjectFileInfo info,
        string include,
        string remove,
        HashSet<string> compileSeen,
        HashSet<string> removedCompile)
    {
        if (remove.Length > 0)
        {
            foreach (var pattern in SplitItemList(remove))
            {
                foreach (var file in PathUtilities.ExpandGlob(info.Directory, pattern))
                {
                    removedCompile.Add(PathUtilities.ForComparison(file));
                }
            }

            return;
        }

        // <Compile Update="..." /> only attaches metadata to an existing item.
        if (include.Length == 0)
        {
            return;
        }

        foreach (var pattern in SplitItemList(include))
        {
            foreach (var file in PathUtilities.ExpandGlob(info.Directory, pattern))
            {
                if (compileSeen.Add(PathUtilities.ForComparison(file)))
                {
                    info.CompileItems.Add(file);
                }
            }
        }
    }

    private static void ReadReferenceItem(
        ProjectFileInfo info,
        XElement item,
        string include,
        IDictionary<string, string> properties)
    {
        if (include.Length == 0)
        {
            return;
        }

        var hintPathRaw = item.Elements().FirstOrDefault(e => LocalName(e) == "HintPath")?.Value
                          ?? item.Attribute("HintPath")?.Value;

        string? hintPath = null;
        if (!string.IsNullOrWhiteSpace(hintPathRaw))
        {
            try
            {
                hintPath = PathUtilities.Normalize(info.Directory, Expand(hintPathRaw!, properties));
            }
            catch (Exception)
            {
                hintPath = null;
            }
        }

        info.References.Add(new AssemblyReferenceInfo(include, hintPath));
    }

    private static void ReadProjectReferenceItem(ProjectFileInfo info, string include)
    {
        if (include.Length == 0)
        {
            return;
        }

        foreach (var pattern in SplitItemList(include))
        {
            try
            {
                info.ProjectReferences.Add(PathUtilities.Normalize(info.Directory, pattern));
            }
            catch (Exception)
            {
                // A malformed ProjectReference must not abort reading the project.
            }
        }
    }

    private static void ReadPackageReferenceItem(
        ProjectFileInfo info,
        XElement item,
        string include,
        IDictionary<string, string> properties)
    {
        if (include.Length == 0)
        {
            return;
        }

        var version = item.Attribute("Version")?.Value
                      ?? item.Elements().FirstOrDefault(e => LocalName(e) == "Version")?.Value
                      ?? string.Empty;

        info.PackageReferences.Add(new KeyValuePair<string, string>(
            include.Trim(),
            Expand(version, properties).Trim()));
    }

    private static void AddSdkDefaultCompileItems(ProjectFileInfo info)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var glob in SdkDefaultCompileGlobs)
        {
            foreach (var file in PathUtilities.ExpandGlob(info.Directory, glob))
            {
                if (PathUtilities.IsUnderIntermediateOutput(info.Directory, file))
                {
                    continue;
                }

                if (seen.Add(PathUtilities.ForComparison(file)))
                {
                    info.CompileItems.Add(file);
                }
            }
        }
    }

    private static void ReadPackagesConfig(ProjectFileInfo info)
    {
        var packagesConfig = Path.Combine(info.Directory, "packages.config");
        if (!File.Exists(packagesConfig))
        {
            return;
        }

        try
        {
            var document = XDocument.Load(packagesConfig);
            foreach (var package in document.Descendants().Where(e => LocalName(e) == "package"))
            {
                var id = package.Attribute("id")?.Value;
                var version = package.Attribute("version")?.Value ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(id))
                {
                    info.PackageReferences.Add(new KeyValuePair<string, string>(id!.Trim(), version.Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            info.Diagnostics.Add($"Failed to read '{packagesConfig}': {ex.Message}");
        }
    }

    private static IEnumerable<string> SplitItemList(string include)
    {
        return include
            .Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
    }

    private static string Get(IDictionary<string, string> properties, string name, string fallback)
    {
        return properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static bool IsTrue(string value) =>
        value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string Expand(string value, IDictionary<string, string> properties)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf("$(", StringComparison.Ordinal) < 0)
        {
            return value;
        }

        return PropertyReference.Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            return properties.TryGetValue(name, out var replacement) ? replacement : string.Empty;
        });
    }

    /// <summary>
    /// Evaluates the small slice of MSBuild condition syntax that legacy
    /// project files actually use: string equality/inequality comparisons and
    /// <c>Exists(...)</c> checks, optionally joined with <c>And</c>/<c>Or</c>.
    /// Anything more exotic is treated as "applies", which errs on the side of
    /// including source files rather than silently dropping them.
    /// </summary>
    private static bool ConditionApplies(
        string? condition,
        IDictionary<string, string> properties,
        string configuration)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var expanded = Expand(condition!, properties).Trim();

        // "Or" is rare in project files and mixing it with "And" would need a
        // real expression parser; be permissive rather than wrong.
        if (Regex.IsMatch(expanded, @"\bOr\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        // Configuration|Platform conditions are the common case and the
        // expansion above already substituted the active values.
        var clauses = Regex.Split(expanded, @"\bAnd\b", RegexOptions.IgnoreCase)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

        return clauses.All(clause => EvaluateClause(clause, configuration));
    }

    private static bool EvaluateClause(string clause, string configuration)
    {
        clause = StripWrappingParentheses(clause);

        if (clause.StartsWith("Exists", StringComparison.OrdinalIgnoreCase))
        {
            var open = clause.IndexOf('(');
            var close = clause.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                var path = clause.Substring(open + 1, close - open - 1).Trim().Trim('\'', '"');
                if (path.Length == 0)
                {
                    return false;
                }

                var normalized = path.Replace('\\', Path.DirectorySeparatorChar);
                return File.Exists(normalized) || System.IO.Directory.Exists(normalized);
            }

            return true;
        }

        var notEqualsIndex = clause.IndexOf("!=", StringComparison.Ordinal);
        if (notEqualsIndex >= 0)
        {
            var left = Unquote(clause.Substring(0, notEqualsIndex));
            var right = Unquote(clause.Substring(notEqualsIndex + 2));
            return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        var equalsIndex = clause.IndexOf("==", StringComparison.Ordinal);
        if (equalsIndex >= 0)
        {
            var left = Unquote(clause.Substring(0, equalsIndex));
            var right = Unquote(clause.Substring(equalsIndex + 2));

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // "Debug|AnyCPU" style comparisons where only the configuration
            // half was substituted.
            return left.StartsWith(configuration + "|", StringComparison.OrdinalIgnoreCase) &&
                   right.StartsWith(configuration + "|", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string Unquote(string value) =>
        value.Trim().Trim('\'', '"').Trim();

    /// <summary>
    /// Removes parentheses that wrap the whole clause — "( a == b )" — while
    /// leaving a function call such as "Exists('x')" intact. Naively trimming
    /// '(' and ')' would strip the closing paren off Exists(...) and make the
    /// clause unparseable.
    /// </summary>
    private static string StripWrappingParentheses(string clause)
    {
        clause = clause.Trim();

        while (clause.Length > 1 && clause[0] == '(' && clause[clause.Length - 1] == ')')
        {
            var depth = 0;
            var wrapsWholeClause = true;

            for (var i = 0; i < clause.Length; i++)
            {
                if (clause[i] == '(')
                {
                    depth++;
                }
                else if (clause[i] == ')')
                {
                    depth--;

                    // Balanced before the end means the leading '(' belongs to
                    // something else, e.g. "Exists('a') And Exists('b')".
                    if (depth == 0 && i < clause.Length - 1)
                    {
                        wrapsWholeClause = false;
                        break;
                    }
                }
            }

            if (!wrapsWholeClause || depth != 0)
            {
                break;
            }

            clause = clause.Substring(1, clause.Length - 2).Trim();
        }

        return clause;
    }
}
