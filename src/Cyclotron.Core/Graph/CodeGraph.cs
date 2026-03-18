using System.Collections.ObjectModel;

namespace Cyclotron.Core.Graph;

public enum CodeNodeKind
{
    Solution,
    Project,
    File,
    Namespace,
    Type,
    Member,
}

public enum CodeEdgeKind
{
    Contains,
    DeclaredIn,
    Inherits,
    Implements,
    Calls,
    UsesType,
    References,
}

public sealed record CodeGraphNode(
    string Id,
    CodeNodeKind Kind,
    string DisplayName,
    string QualifiedName,
    string? FilePath = null,
    IReadOnlyDictionary<string, string?>? Metadata = null);

public sealed record CodeGraphEdge(
    string FromId,
    string ToId,
    CodeEdgeKind Kind,
    string? Label = null);

public sealed class CodeGraph
{
    private readonly IReadOnlyDictionary<string, CodeGraphNode> _nodesById;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CodeGraphEdge>> _outgoing;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CodeGraphEdge>> _incoming;

    public CodeGraph(IEnumerable<CodeGraphNode> nodes, IEnumerable<CodeGraphEdge> edges)
    {
        Nodes = nodes
            .OrderBy(node => node.Kind)
            .ThenBy(node => node.QualifiedName, StringComparer.Ordinal)
            .ToArray();

        Edges = edges
            .OrderBy(edge => edge.Kind)
            .ThenBy(edge => edge.FromId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToId, StringComparer.Ordinal)
            .ToArray();

        _nodesById = Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        _outgoing = BuildAdjacency(Edges, edge => edge.FromId);
        _incoming = BuildAdjacency(Edges, edge => edge.ToId);
    }

    public IReadOnlyList<CodeGraphNode> Nodes { get; }

    public IReadOnlyList<CodeGraphEdge> Edges { get; }

    public bool TryGetNode(string id, out CodeGraphNode node) => _nodesById.TryGetValue(id, out node!);

    public IReadOnlyList<CodeGraphEdge> GetOutgoing(string nodeId, CodeEdgeKind? kind = null)
    {
        if (!_outgoing.TryGetValue(nodeId, out var edges))
        {
            return Array.Empty<CodeGraphEdge>();
        }

        return kind is null
            ? edges
            : edges.Where(edge => edge.Kind == kind.Value).ToArray();
    }

    public IReadOnlyList<CodeGraphEdge> GetIncoming(string nodeId, CodeEdgeKind? kind = null)
    {
        if (!_incoming.TryGetValue(nodeId, out var edges))
        {
            return Array.Empty<CodeGraphEdge>();
        }

        return kind is null
            ? edges
            : edges.Where(edge => edge.Kind == kind.Value).ToArray();
    }

    public IReadOnlyList<CodeGraphNode> SearchNodes(string query, params CodeNodeKind[] allowedKinds)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<CodeGraphNode>();
        }

        var trimmed = query.Trim();
        var hasKindFilter = allowedKinds.Length > 0;

        return Nodes
            .Where(node => !hasKindFilter || allowedKinds.Contains(node.Kind))
            .Select(node => new
            {
                Node = node,
                Score = ComputeScore(node, trimmed),
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Node.QualifiedName, StringComparer.Ordinal)
            .Select(result => result.Node)
            .Take(25)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CodeGraphEdge>> BuildAdjacency(
        IEnumerable<CodeGraphEdge> edges,
        Func<CodeGraphEdge, string> keySelector)
    {
        return new ReadOnlyDictionary<string, IReadOnlyList<CodeGraphEdge>>(
            edges.GroupBy(keySelector, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<CodeGraphEdge>)group.ToArray(),
                    StringComparer.Ordinal));
    }

    private static int ComputeScore(CodeGraphNode node, string query)
    {
        if (string.Equals(node.Id, query, StringComparison.Ordinal))
        {
            return 100;
        }

        if (string.Equals(node.QualifiedName, query, StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (string.Equals(node.DisplayName, query, StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        if (node.QualifiedName.EndsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        if (node.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (node.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) is true)
        {
            return 50;
        }

        return 0;
    }
}
