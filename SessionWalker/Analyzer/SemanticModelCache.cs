using Microsoft.CodeAnalysis;
using SessionWalker.Core.Interfaces;
using System.Collections.Concurrent;

namespace SessionWalker.Analyzer;

/// <summary>
/// Thread-safe cache keyed by SyntaxTree. A single instance is shared across
/// all analyzers for the lifetime of one CLI invocation (see
/// AnalysisOrchestrator), which is what lets SessionUsageAnalyzer's
/// interprocedural tracing jump into another method's tree without
/// re-binding it if a prior analyzer (or an earlier call site) already did.
/// </summary>
public sealed class SemanticModelCache : ISemanticModelCache
{
    private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _cache = new();

    public SemanticModel GetSemanticModel(Compilation compilation, SyntaxTree tree)
    {
        return _cache.GetOrAdd(tree, t => compilation.GetSemanticModel(t));
    }
}
