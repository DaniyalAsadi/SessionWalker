using System.Threading;
using System.Threading.Tasks;
using SessionWalker.Core.Models;

namespace SessionWalker.Core.Interfaces;

/// <summary>
/// Extensibility seam for output formats. Console/JSON/CSV ship in-box;
/// additional formats (e.g. SARIF, JUnit XML for CI) can be added by
/// implementing this interface and registering it in the CLI's DI container.
/// </summary>
public interface IOutputWriter
{
    /// <summary>Format identifier used for CLI wiring/logging, e.g. "json", "csv", "console".</summary>
    string FormatName { get; }

    Task WriteAsync(AnalysisResult result, string destinationPath, CancellationToken cancellationToken);
}
