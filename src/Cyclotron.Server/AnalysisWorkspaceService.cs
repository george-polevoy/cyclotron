using Cyclotron.Core.Analysis;
using Cyclotron.Core.Graph;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Cyclotron.Server;

public sealed class AnalysisWorkspaceService
{
    private readonly CodebaseAnalyzer _analyzer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CodeWorkspaceSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AnalysisWorkspaceService(CodebaseAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    public async Task<CodeWorkspaceSnapshot> GetSnapshotAsync(
        string targetPath,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(targetPath);

        if (!forceRefresh && _cache.TryGetValue(fullPath, out var existing))
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(fullPath, out existing))
            {
                return existing;
            }

            var snapshot = await _analyzer.AnalyzeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            _cache[fullPath] = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public SymbolResolution ResolveSymbol(CodeWorkspaceSnapshot workspaceSnapshot, string query, params CodeNodeKind[] allowedKinds)
    {
        var candidates = workspaceSnapshot.Snapshot.Graph.SearchNodes(query, allowedKinds)
            .Select(node => new SymbolCandidate(node.Id, node.DisplayName, node.QualifiedName, node.Kind.ToString(), node.FilePath))
            .ToArray();

        if (candidates.Length == 0)
        {
            return new SymbolResolution(null, Array.Empty<SymbolCandidate>(), $"No symbols matched '{query}'.");
        }

        var idMatches = candidates
            .Where(candidate => string.Equals(candidate.SymbolId, query, StringComparison.Ordinal))
            .ToArray();
        var qualifiedMatches = candidates
            .Where(candidate => string.Equals(candidate.QualifiedName, query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nameMatches = candidates
            .Where(candidate => string.Equals(candidate.Name, query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var selected = idMatches.Length == 1
            ? idMatches[0]
            : qualifiedMatches.Length == 1
                ? qualifiedMatches[0]
                : nameMatches.Length >= 1
                    ? nameMatches[0]
                    : candidates[0];

        string? message = null;
        if (nameMatches.Length > 1)
        {
            message = $"Query '{query}' matched multiple symbols; using '{selected.QualifiedName}'. Use search_symbols for a narrower match if needed.";
        }
        else if (candidates.Length > 1 &&
                 !string.Equals(selected.SymbolId, query, StringComparison.Ordinal) &&
                 !string.Equals(selected.QualifiedName, query, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Resolved '{query}' to '{selected.QualifiedName}'. Use search_symbols for a narrower match if needed.";
        }

        return new SymbolResolution(selected, candidates, message);
    }

    public async Task<IReadOnlyList<UsageLocation>> FindUsagesAsync(
        CodeWorkspaceSnapshot workspaceSnapshot,
        string symbolId,
        CancellationToken cancellationToken)
    {
        if (!workspaceSnapshot.SymbolsById.TryGetValue(symbolId, out var symbol))
        {
            return Array.Empty<UsageLocation>();
        }

        var solution = workspaceSnapshot.SolutionsBySymbolId.TryGetValue(symbolId, out var symbolSolution)
            ? symbolSolution
            : workspaceSnapshot.Solution;
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
        var usages = new List<UsageLocation>();

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
            {
                var document = location.Document;
                if (document is null)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var linePosition = text.Lines.GetLinePosition(location.Location.SourceSpan.Start);

                usages.Add(new UsageLocation(
                    document.Project.Name,
                    document.FilePath ?? document.Name,
                    linePosition.Line + 1,
                    linePosition.Character + 1,
                    location.IsCandidateLocation,
                    reference.Definition.ToDisplayString()));
            }
        }

        return usages
            .OrderBy(usage => usage.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(usage => usage.Line)
            .ThenBy(usage => usage.Column)
            .ToArray();
    }
}

public sealed record SymbolCandidate(
    string SymbolId,
    string Name,
    string QualifiedName,
    string Kind,
    string? FilePath);

public sealed record SymbolResolution(
    SymbolCandidate? Selected,
    IReadOnlyList<SymbolCandidate> Candidates,
    string? Message);

public sealed record UsageLocation(
    string Project,
    string FilePath,
    int Line,
    int Column,
    bool IsCandidate,
    string Definition);
