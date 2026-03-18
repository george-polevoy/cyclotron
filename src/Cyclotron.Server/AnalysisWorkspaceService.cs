using Cyclotron.Core.Analysis;
using Cyclotron.Core.Graph;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Cyclotron.Server;

public sealed class AnalysisWorkspaceService
{
    private readonly CodebaseAnalyzer _analyzer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

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
        var sourceStamp = ComputeSourceStamp(fullPath);

        if (!forceRefresh &&
            _cache.TryGetValue(fullPath, out var existing) &&
            existing.SourceStamp == sourceStamp)
        {
            return existing.Snapshot;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            sourceStamp = ComputeSourceStamp(fullPath);
            if (!forceRefresh &&
                _cache.TryGetValue(fullPath, out existing) &&
                existing.SourceStamp == sourceStamp)
            {
                return existing.Snapshot;
            }

            var snapshot = await _analyzer.AnalyzeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            _cache[fullPath] = new CacheEntry(snapshot, sourceStamp);
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
                    ToRelativePath(document.FilePath ?? document.Name, workspaceSnapshot.Snapshot.AnalysisRootPath),
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

    public async Task<CodeWorkspaceSnapshot> GetApiSnapshotAsync(
        string targetPath,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var rawSnapshot = await GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        return RelativizeSnapshot(rawSnapshot, NormalizeRequestedTargetPath(targetPath, rawSnapshot.Snapshot.AnalysisRootPath));
    }

    public static string NormalizeRequestedTargetPath(string targetPath, string analysisRootPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("targetPath must be a non-empty path.", nameof(targetPath));
        }

        var fullPath = Path.GetFullPath(targetPath, Directory.GetCurrentDirectory());
        return ToRelativePath(fullPath, analysisRootPath);
    }

    private static CodeWorkspaceSnapshot RelativizeSnapshot(CodeWorkspaceSnapshot rawSnapshot, string requestedTargetPath)
    {
        var rootPath = rawSnapshot.Snapshot.AnalysisRootPath;
        var nodeIdMap = rawSnapshot.Snapshot.Graph.Nodes.ToDictionary(
            node => node.Id,
            node => RewriteNodeId(node.Id, rootPath, requestedTargetPath),
            StringComparer.Ordinal);

        var relativizedNodes = rawSnapshot.Snapshot.Graph.Nodes
            .Select(node => node with
            {
                Id = nodeIdMap[node.Id],
                QualifiedName = RewriteNodeQualifiedName(node, rootPath, requestedTargetPath),
                FilePath = RewritePath(node.FilePath, rootPath),
            })
            .ToArray();

        var relativizedEdges = rawSnapshot.Snapshot.Graph.Edges
            .Select(edge => edge with
            {
                FromId = nodeIdMap.TryGetValue(edge.FromId, out var fromId) ? fromId : edge.FromId,
                ToId = nodeIdMap.TryGetValue(edge.ToId, out var toId) ? toId : edge.ToId,
            })
            .ToArray();

        var relativizedSnapshot = rawSnapshot.Snapshot with
        {
            TargetPath = requestedTargetPath,
            AnalysisRootPath = ".",
            Graph = new CodeGraph(relativizedNodes, relativizedEdges),
            MemberMetrics = rawSnapshot.Snapshot.MemberMetrics
                .Select(metric => metric with { FilePath = RewritePath(metric.FilePath, rootPath) })
                .ToArray(),
            TypeMetrics = rawSnapshot.Snapshot.TypeMetrics
                .Select(metric => metric with { FilePath = RewritePath(metric.FilePath, rootPath) })
                .ToArray(),
            Diagnostics = rawSnapshot.Snapshot.Diagnostics
                .Select(message => RewriteDiagnostic(message, rootPath))
                .ToArray(),
        };

        return new CodeWorkspaceSnapshot(
            relativizedSnapshot,
            rawSnapshot.Solution,
            rawSnapshot.SymbolsById,
            rawSnapshot.SolutionsBySymbolId);
    }

    private static string RewriteDiagnostic(string message, string rootPath)
    {
        var rootWithSeparator = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return message
            .Replace(rootWithSeparator, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(rootPath, ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string RewriteNodeId(string nodeId, string rootPath, string requestedTargetPath)
    {
        if (nodeId.StartsWith("solution:", StringComparison.Ordinal))
        {
            return $"solution:{requestedTargetPath}";
        }

        if (nodeId.StartsWith("project:", StringComparison.Ordinal))
        {
            var projectPath = nodeId["project:".Length..];
            return $"project:{ToRelativePath(projectPath, rootPath)}";
        }

        if (nodeId.StartsWith("file:", StringComparison.Ordinal))
        {
            var filePath = nodeId["file:".Length..];
            return $"file:{ToRelativePath(filePath, rootPath)}";
        }

        return nodeId;
    }

    private static string RewriteNodeQualifiedName(CodeGraphNode node, string rootPath, string requestedTargetPath)
    {
        return node.Kind switch
        {
            CodeNodeKind.Solution => requestedTargetPath,
            CodeNodeKind.Project when node.Id.StartsWith("project:", StringComparison.Ordinal) => RewritePath(node.QualifiedName, rootPath) ?? node.QualifiedName,
            CodeNodeKind.File => RewritePath(node.QualifiedName, rootPath) ?? node.QualifiedName,
            _ => node.QualifiedName,
        };
    }

    private static string? RewritePath(string? path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path)
            ? ToRelativePath(path, rootPath)
            : path;
    }

    private static string ToRelativePath(string path, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, Path.GetFullPath(path, rootPath));
        return string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;
    }

    private static SourceStamp ComputeSourceStamp(string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return ComputeSourceStampForDirectory(Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory());
        }

        if (Directory.Exists(targetPath))
        {
            return ComputeSourceStampForDirectory(targetPath);
        }

        throw new DirectoryNotFoundException($"Target path '{targetPath}' does not exist.");
    }

    private static SourceStamp ComputeSourceStampForDirectory(string directoryPath)
    {
        var relevantFiles = Directory
            .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Where(IsRelevantSourceFile)
            .ToArray();

        long latestWriteTicks = 0;
        foreach (var file in relevantFiles)
        {
            var lastWriteTicks = File.GetLastWriteTimeUtc(file).Ticks;
            if (lastWriteTicks > latestWriteTicks)
            {
                latestWriteTicks = lastWriteTicks;
            }
        }

        return new SourceStamp(relevantFiles.Length, latestWriteTicks);
    }

    private static bool IsRelevantSourceFile(string path)
    {
        if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CacheEntry(CodeWorkspaceSnapshot Snapshot, SourceStamp SourceStamp);

    private readonly record struct SourceStamp(int FileCount, long LatestWriteTicks);
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
