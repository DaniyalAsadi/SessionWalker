using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SessionWalker.Core.Interfaces;
using SessionWalker.Core.Models;

namespace SessionWalker.Infrastructure.Output;

/// <summary>
/// Flat CSV, one row per Session operation, suitable for Excel/PowerBI —
/// per design brief section 16. Column order matches the spec exactly.
/// </summary>
public sealed class CsvOutputWriter : IOutputWriter
{
    public string FormatName => "csv";

    private static readonly string[] _header =
    {
        "Project", "Namespace", "Controller", "Action", "File", "Line", "Column",
        "Operation", "Mutation", "Key", "Expression", "Confidence", "CanBeReadOnly"
    };

    public async Task WriteAsync(AnalysisResult result, string destinationPath, CancellationToken cancellationToken)
    {
        var actionReadOnlyLookup = result.AllActions
            .ToDictionary(a => (a.ProjectName, a.ControllerName, a.ActionName), a => a.CanPotentiallyUseReadOnlySession);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", _header));

        foreach (var op in result.AllSessionOperations)
        {
            var canBeReadOnly = "N/A";
            if (op.Symbol.ControllerName is not null && op.Symbol.ActionName is not null
                && actionReadOnlyLookup.TryGetValue((op.Symbol.ProjectName ?? string.Empty, op.Symbol.ControllerName, op.Symbol.ActionName), out var value))
            {
                canBeReadOnly = value ? "YES" : "NO";
            }

            var row = new[]
            {
                op.Symbol.ProjectName,
                op.Symbol.Namespace,
                op.Symbol.ControllerName,
                op.Symbol.ActionName,
                op.Location.FilePath,
                op.Location.Line.ToString(CultureInfo.InvariantCulture),
                op.Location.Column.ToString(CultureInfo.InvariantCulture),
                op.OperationType.ToString(),
                op.MutationKind?.ToString() ?? string.Empty,
                op.Key ?? string.Empty,
                op.Expression,
                op.Confidence.ToString(),
                canBeReadOnly
            };

            sb.AppendLine(string.Join(",", row.Select(Escape)));
        }

        if (string.IsNullOrEmpty(destinationPath))
        {
            await Console.Out.WriteAsync(sb.ToString()).ConfigureAwait(false);
        }
        else
        {
            File.WriteAllText(destinationPath, sb.ToString());
        }
    }

    private static string Escape(string value)
    {
        value ??= string.Empty;
        var needsQuoting = Enumerable.Contains(value, ',') || Enumerable.Contains(value, '"') || Enumerable.Contains(value, '\n') || Enumerable.Contains(value, '\r');
        if (!needsQuoting)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
