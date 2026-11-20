using Cyclotron.Core.Graph;
using Microsoft.CodeAnalysis;

namespace Cyclotron.Core.Analysis;

public sealed record MemberMetric(
    string SymbolId,
    string Name,
    string QualifiedName,
    string MemberKind,
    string ContainingTypeId,
    int CyclomaticComplexity,
    string? FilePath);

public sealed record AllocationSiteSummary(
    int Index,
    string Kind,
    string? TypeName,
    bool EscapesViaReturn,
    bool EscapesViaMember,
    bool EscapesViaArgument,
    bool CapturedByClosure,
    bool HasUnknownEscape);

public sealed record MethodBodySummary(
    string SymbolId,
    string Name,
    string QualifiedName,
    string MemberKind,
    string ContainingTypeId,
    int AllocationCount,
    IReadOnlyList<AllocationSiteSummary> Allocations,
    bool HasReturnEscape,
    bool HasFieldOrPropertyEscape,
    bool HasArgumentEscape,
    bool HasCapturedVariableEscape,
    bool HasUnknownEscape,
    bool HasSideEffects,
    bool IsPureLike,
    string? FilePath);

public sealed record TypeMetric(
    string SymbolId,
    string Name,
    string QualifiedName,
    string TypeKind,
    int MethodCount,
    int CyclomaticComplexity,
    int MaxMethodCyclomaticComplexity,
    double CohesionScore,
    int AfferentCoupling,
    int EfferentCoupling,
    int ExternalCoupling,
    double Instability,
    string? FilePath);

public sealed record GraphOverview(
    int NodeCount,
    int EdgeCount,
    int TypeCount,
    int MemberCount,
    int CycleCount,
    double DependencyDensity);

public sealed record GraphHotspot(
    string SymbolId,
    string Name,
    string QualifiedName,
    double RiskScore,
    int CyclomaticComplexity,
    int TotalCoupling,
    double CohesionScore,
    double Instability,
    double BetweennessCentrality,
    bool InCycle);

public sealed record GraphCycleRegion(
    IReadOnlyList<string> SymbolIds,
    IReadOnlyList<string> SymbolNames,
    double AverageComplexity,
    double AverageCoupling);

public sealed record GraphBroker(
    string SymbolId,
    string Name,
    string QualifiedName,
    double BetweennessCentrality,
    int NeighborCount);

public sealed record GraphQualitySignals(
    GraphOverview Overview,
    IReadOnlyList<GraphHotspot> Hotspots,
    IReadOnlyList<GraphCycleRegion> CycleRegions,
    IReadOnlyList<GraphBroker> Brokers);

public sealed record CodebaseSnapshot(
    string TargetPath,
    string AnalysisRootPath,
    DateTimeOffset AnalyzedAtUtc,
    CodeGraph Graph,
    IReadOnlyList<MemberMetric> MemberMetrics,
    IReadOnlyList<MethodBodySummary> MethodBodySummaries,
    IReadOnlyList<TypeMetric> TypeMetrics,
    GraphQualitySignals Signals,
    IReadOnlyList<string> Diagnostics);

public sealed class CodeWorkspaceSnapshot
{
    public CodeWorkspaceSnapshot(
        CodebaseSnapshot snapshot,
        Solution solution,
        IReadOnlyDictionary<string, ISymbol> symbolsById,
        IReadOnlyDictionary<string, Solution> solutionsBySymbolId)
    {
        Snapshot = snapshot;
        Solution = solution;
        SymbolsById = symbolsById;
        SolutionsBySymbolId = solutionsBySymbolId;
    }

    public CodebaseSnapshot Snapshot { get; }

    public Solution Solution { get; }

    public IReadOnlyDictionary<string, ISymbol> SymbolsById { get; }

    public IReadOnlyDictionary<string, Solution> SolutionsBySymbolId { get; }
}
