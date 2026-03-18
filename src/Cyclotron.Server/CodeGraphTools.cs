using System.ComponentModel;
using Cyclotron.Core.Analysis;
using Cyclotron.Core.Graph;
using ModelContextProtocol.Server;

namespace Cyclotron.Server;

[McpServerToolType]
public sealed class CodeGraphTools
{
    private readonly AnalysisWorkspaceService _workspaceService;

    public CodeGraphTools(AnalysisWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [McpServerTool, Description("Analyze a C# codebase and build a graph of symbols, relationships, metrics, and graph-quality signals.")]
    public async Task<AnalyzeCodebaseResponse> AnalyzeCodebase(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        var snapshot = workspaceSnapshot.Snapshot;

        return new AnalyzeCodebaseResponse(
            snapshot.TargetPath,
            snapshot.AnalyzedAtUtc,
            snapshot.Signals.Overview,
            snapshot.Diagnostics,
            snapshot.Signals.Hotspots.Take(5).ToArray(),
            snapshot.Signals.CycleRegions.Take(5).ToArray(),
            snapshot.Signals.Brokers.Take(5).ToArray());
    }

    [McpServerTool, Description("Search for symbols in the analyzed graph by simple name, qualified name, or path fragment.")]
    public async Task<SearchSymbolsResponse> SearchSymbols(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("Search term for a type, member, namespace, or file.")] string query,
        [Description("Optional comma-separated filter such as 'Type,Member'.")] string? kinds = null,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        var allowedKinds = ParseKinds(kinds);
        var candidates = workspaceSnapshot.Snapshot.Graph.SearchNodes(query, allowedKinds)
            .Select(node => new SymbolCandidate(node.Id, node.DisplayName, node.QualifiedName, node.Kind.ToString(), node.FilePath))
            .ToArray();

        return new SearchSymbolsResponse(query, candidates);
    }

    [McpServerTool, Description("Return base classes, implemented interfaces, and derived types for a class or interface.")]
    public async Task<ClassHierarchyResponse> GetClassHierarchy(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("Type name or qualified name to inspect.")] string symbolQuery,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        var resolution = _workspaceService.ResolveSymbol(workspaceSnapshot, symbolQuery, CodeNodeKind.Type);
        if (resolution.Selected is null)
        {
            return new ClassHierarchyResponse(resolution.Message ?? "Symbol not found.", null, Array.Empty<SymbolCandidate>(), Array.Empty<SymbolCandidate>(), Array.Empty<SymbolCandidate>(), Array.Empty<SymbolCandidate>());
        }

        var graph = workspaceSnapshot.Snapshot.Graph;
        var selectedNode = graph.Nodes.Single(node => string.Equals(node.Id, resolution.Selected.SymbolId, StringComparison.Ordinal));

        var baseTypes = graph.GetOutgoing(selectedNode.Id)
            .Where(edge => edge.Kind == CodeEdgeKind.Inherits)
            .Select(edge => ToCandidate(graph, edge.ToId))
            .Where(candidate => candidate is not null)
            .Cast<SymbolCandidate>()
            .ToArray();

        var implementedInterfaces = graph.GetOutgoing(selectedNode.Id)
            .Where(edge => edge.Kind == CodeEdgeKind.Implements)
            .Select(edge => ToCandidate(graph, edge.ToId))
            .Where(candidate => candidate is not null)
            .Cast<SymbolCandidate>()
            .ToArray();

        var derivedTypes = graph.GetIncoming(selectedNode.Id)
            .Where(edge => edge.Kind == CodeEdgeKind.Inherits)
            .Select(edge => ToCandidate(graph, edge.FromId))
            .Where(candidate => candidate is not null)
            .Cast<SymbolCandidate>()
            .ToArray();

        var implementers = graph.GetIncoming(selectedNode.Id)
            .Where(edge => edge.Kind == CodeEdgeKind.Implements)
            .Select(edge => ToCandidate(graph, edge.FromId))
            .Where(candidate => candidate is not null)
            .Cast<SymbolCandidate>()
            .ToArray();

        return new ClassHierarchyResponse(
            resolution.Message,
            resolution.Selected,
            baseTypes,
            implementedInterfaces,
            derivedTypes,
            implementers);
    }

    [McpServerTool, Description("Find source usages of a code unit using Roslyn symbol references.")]
    public async Task<FindUsagesResponse> FindSymbolUsages(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("Type or member name to inspect.")] string symbolQuery,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        var resolution = _workspaceService.ResolveSymbol(workspaceSnapshot, symbolQuery, CodeNodeKind.Type, CodeNodeKind.Member, CodeNodeKind.Namespace);
        if (resolution.Selected is null)
        {
            return new FindUsagesResponse(resolution.Message ?? "Symbol not found.", null, Array.Empty<UsageLocation>(), resolution.Candidates);
        }

        var usages = await _workspaceService.FindUsagesAsync(workspaceSnapshot, resolution.Selected.SymbolId, cancellationToken).ConfigureAwait(false);
        return new FindUsagesResponse(resolution.Message, resolution.Selected, usages, resolution.Candidates);
    }

    [McpServerTool, Description("Return code metrics for a type or member, or the top hotspots when no symbol is provided.")]
    public async Task<GetCodeMetricsResponse> GetCodeMetrics(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("Optional type or member name. Leave empty to get top hotspots.")] string? symbolQuery = null,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        var snapshot = workspaceSnapshot.Snapshot;

        if (string.IsNullOrWhiteSpace(symbolQuery))
        {
            return new GetCodeMetricsResponse(
                null,
                null,
                snapshot.TypeMetrics.OrderByDescending(metric => metric.CyclomaticComplexity).Take(10).ToArray(),
                snapshot.MemberMetrics.OrderByDescending(metric => metric.CyclomaticComplexity).Take(10).ToArray(),
                snapshot.Signals.Hotspots.Take(10).ToArray());
        }

        var resolution = _workspaceService.ResolveSymbol(workspaceSnapshot, symbolQuery, CodeNodeKind.Type, CodeNodeKind.Member);
        if (resolution.Selected is null)
        {
            return new GetCodeMetricsResponse(resolution.Message ?? "Symbol not found.", null, Array.Empty<TypeMetric>(), Array.Empty<MemberMetric>(), Array.Empty<GraphHotspot>());
        }

        var typeMetrics = snapshot.TypeMetrics.Where(metric => string.Equals(metric.SymbolId, resolution.Selected.SymbolId, StringComparison.Ordinal)).ToArray();
        var memberMetrics = snapshot.MemberMetrics.Where(metric => string.Equals(metric.SymbolId, resolution.Selected.SymbolId, StringComparison.Ordinal)).ToArray();
        var hotspots = snapshot.Signals.Hotspots.Where(hotspot => string.Equals(hotspot.SymbolId, resolution.Selected.SymbolId, StringComparison.Ordinal)).ToArray();

        return new GetCodeMetricsResponse(resolution.Message, resolution.Selected, typeMetrics, memberMetrics, hotspots);
    }

    [McpServerTool, Description("Traverse the code graph using breadth-first search from a starting symbol.")]
    public async Task<BfsResponse> BfsGraph(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("Type or member name to start from.")] string startSymbol,
        [Description("Maximum traversal depth.")] int maxDepth = 2,
        [Description("Optional comma-separated edge kinds such as 'Calls,UsesType'.")] string? edgeKinds = null,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        var resolution = _workspaceService.ResolveSymbol(workspaceSnapshot, startSymbol, CodeNodeKind.Type, CodeNodeKind.Member, CodeNodeKind.Namespace, CodeNodeKind.File);
        if (resolution.Selected is null)
        {
            return new BfsResponse(resolution.Message ?? "Symbol not found.", null, Array.Empty<BfsVisit>(), resolution.Candidates);
        }

        var graph = workspaceSnapshot.Snapshot.Graph;
        var allowedKinds = ParseEdgeKinds(edgeKinds);
        var visited = new HashSet<string>(StringComparer.Ordinal) { resolution.Selected.SymbolId };
        var queue = new Queue<(string NodeId, int Depth)>();
        var visits = new List<BfsVisit> { new(resolution.Selected.SymbolId, resolution.Selected.Name, resolution.Selected.QualifiedName, resolution.Selected.Kind, 0, null, null) };
        queue.Enqueue((resolution.Selected.SymbolId, 0));

        while (queue.Count > 0)
        {
            var (nodeId, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var edge in graph.GetOutgoing(nodeId))
            {
                if (allowedKinds.Length > 0 && !allowedKinds.Contains(edge.Kind))
                {
                    continue;
                }

                if (!visited.Add(edge.ToId))
                {
                    continue;
                }

                if (!graph.TryGetNode(edge.ToId, out var node))
                {
                    continue;
                }

                visits.Add(new BfsVisit(node.Id, node.DisplayName, node.QualifiedName, node.Kind.ToString(), depth + 1, edge.Kind.ToString(), nodeId));
                queue.Enqueue((node.Id, depth + 1));
            }
        }

        return new BfsResponse(resolution.Message, resolution.Selected, visits, resolution.Candidates);
    }

    [McpServerTool, Description("Return graph-based quality signals such as hotspots, cyclic regions, and broker nodes.")]
    public async Task<GraphSignalsResponse> GetGraphQualitySignals(
        [Description("Path to a directory, solution, or project file.")] string targetPath,
        [Description("When true, rebuild the graph instead of using the cached snapshot.")] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceSnapshot = await _workspaceService.GetSnapshotAsync(targetPath, forceRefresh, cancellationToken).ConfigureAwait(false);
        return new GraphSignalsResponse(
            workspaceSnapshot.Snapshot.Signals.Overview,
            workspaceSnapshot.Snapshot.Signals.Hotspots,
            workspaceSnapshot.Snapshot.Signals.CycleRegions,
            workspaceSnapshot.Snapshot.Signals.Brokers);
    }

    private static SymbolCandidate? ToCandidate(CodeGraph graph, string symbolId)
    {
        return graph.TryGetNode(symbolId, out var node)
            ? new SymbolCandidate(node.Id, node.DisplayName, node.QualifiedName, node.Kind.ToString(), node.FilePath)
            : null;
    }

    private static CodeNodeKind[] ParseKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
        {
            return Array.Empty<CodeNodeKind>();
        }

        return kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(kind => Enum.TryParse<CodeNodeKind>(kind, true, out var parsed) ? parsed : (CodeNodeKind?)null)
            .Where(kind => kind.HasValue)
            .Select(kind => kind!.Value)
            .ToArray();
    }

    private static CodeEdgeKind[] ParseEdgeKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
        {
            return Array.Empty<CodeEdgeKind>();
        }

        return kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(kind => Enum.TryParse<CodeEdgeKind>(kind, true, out var parsed) ? parsed : (CodeEdgeKind?)null)
            .Where(kind => kind.HasValue)
            .Select(kind => kind!.Value)
            .ToArray();
    }
}

public sealed record AnalyzeCodebaseResponse(
    string TargetPath,
    DateTimeOffset AnalyzedAtUtc,
    GraphOverview Overview,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<GraphHotspot> Hotspots,
    IReadOnlyList<GraphCycleRegion> CycleRegions,
    IReadOnlyList<GraphBroker> Brokers);

public sealed record SearchSymbolsResponse(
    string Query,
    IReadOnlyList<SymbolCandidate> Matches);

public sealed record ClassHierarchyResponse(
    string? Message,
    SymbolCandidate? Symbol,
    IReadOnlyList<SymbolCandidate> BaseTypes,
    IReadOnlyList<SymbolCandidate> ImplementedInterfaces,
    IReadOnlyList<SymbolCandidate> DerivedTypes,
    IReadOnlyList<SymbolCandidate> Implementers);

public sealed record FindUsagesResponse(
    string? Message,
    SymbolCandidate? Symbol,
    IReadOnlyList<UsageLocation> Usages,
    IReadOnlyList<SymbolCandidate> Candidates);

public sealed record GetCodeMetricsResponse(
    string? Message,
    SymbolCandidate? Symbol,
    IReadOnlyList<TypeMetric> TypeMetrics,
    IReadOnlyList<MemberMetric> MemberMetrics,
    IReadOnlyList<GraphHotspot> Hotspots);

public sealed record BfsVisit(
    string SymbolId,
    string Name,
    string QualifiedName,
    string Kind,
    int Depth,
    string? ViaEdgeKind,
    string? FromSymbolId);

public sealed record BfsResponse(
    string? Message,
    SymbolCandidate? StartSymbol,
    IReadOnlyList<BfsVisit> Visits,
    IReadOnlyList<SymbolCandidate> Candidates);

public sealed record GraphSignalsResponse(
    GraphOverview Overview,
    IReadOnlyList<GraphHotspot> Hotspots,
    IReadOnlyList<GraphCycleRegion> CycleRegions,
    IReadOnlyList<GraphBroker> Brokers);
