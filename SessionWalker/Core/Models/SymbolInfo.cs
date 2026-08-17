namespace SessionWalker.Core.Models;

/// <summary>
/// Lightweight, serializable snapshot of the semantic containment chain for a
/// given syntax node. Populated best-effort — any field may be null when the
/// node is not inside that kind of container (e.g. a top-level helper class
/// has no ControllerName/ActionName).
/// </summary>
public sealed record SymbolInfo(
    string SolutionPath,
    string ProjectName,
    string AssemblyName,
    string Namespace,
    string ClassName,
    string MethodName,
    string ControllerName,
    string ActionName)
{
    public string SolutionPath { get; } = SolutionPath;
    public string ProjectName { get; } = ProjectName;
    public string AssemblyName { get; } = AssemblyName;
    public string Namespace { get; } = Namespace;
    public string ClassName { get; } = ClassName;
    public string MethodName { get; } = MethodName;
    public string ControllerName { get; } = ControllerName;
    public string ActionName { get; } = ActionName;
}
