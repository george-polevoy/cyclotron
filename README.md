# Cyclotron

Cyclotron is a Roslyn-based code intelligence server for coding agents. It builds a graph from C# source code, exposes that graph over MCP, and layers structural metrics and graph-quality signals on top of symbol relationships.

## What it does today

- Builds a code graph from a `.sln`, `.csproj`, or a directory of `.cs` files
- Exposes MCP tools for:
  - codebase analysis snapshots
  - symbol search
  - class hierarchy lookup
  - symbol usage lookup
  - code metrics lookup
  - BFS graph traversal
  - graph-quality signal summaries
- Computes baseline metrics:
  - cyclomatic complexity at member and type level
  - cohesion as shared-field connectivity across methods
  - afferent/efferent coupling and instability
- Extracts graph-informed quality signals:
  - cyclic regions via strongly connected components
  - broker types via betweenness centrality
  - multi-factor hotspots that combine complexity, coupling, low cohesion, and cycle participation

## Why the graph matters

Cyclotron is aimed at more than code search. The graph representation creates room for higher-level quality heuristics:

- Dense cyclic regions often correlate with brittle change surfaces and hidden architectural coupling.
- High-betweenness "broker" types can reveal coordination bottlenecks that quietly concentrate risk.
- Low-cohesion, high-coupling nodes are good candidates for direct code-quality estimation because they combine structural spread with poor internal focus.
- The same graph substrate can later support richer analyses such as ownership boundaries, escape-style dataflow approximations, or architectural drift detection.

## Layout

- `src/Cyclotron.Core`: Roslyn loading, graph extraction, metrics, and graph algorithms
- `src/Cyclotron.Server`: MCP host and tool surface
- `samples/SampleCodebase`: small sample project for testing and demos
- `tests/Cyclotron.Tests`: analyzer regression tests

## Running

Build everything:

```bash
dotnet build Cyclotron.sln
```

Run the MCP server over stdio:

```bash
dotnet run --project src/Cyclotron.Server
```

The server exposes tools named from the public methods in `CodeGraphTools`, including:

- `AnalyzeCodebase`
- `SearchSymbols`
- `GetClassHierarchy`
- `FindSymbolUsages`
- `GetCodeMetrics`
- `BfsGraph`
- `GetGraphQualitySignals`

## Runtime state

Cyclotron keeps analyzed codebases in RAM for the lifetime of the running MCP server process.

- The cache is process-wide, not per request or per MCP conversation.
- Cache entries are keyed by analyzed target path, so one server process can keep multiple repositories hot at once.
- Repeated queries against an unchanged target reuse the in-memory Roslyn snapshot instead of rebuilding it.
- If `.cs`, `.csproj`, `.sln`, `.props`, or `.targets` files change, Cyclotron automatically refreshes that cached entry on the next query.
- `forceRefresh=true` bypasses the cache and rebuilds immediately.
- Restarting the MCP server clears all in-memory state.

## Next useful steps

1. Add a first-class query DSL over the graph instead of tool-by-tool traversal.
2. Expand the graph beyond declarations and calls into control/data flow edges.
3. Introduce escape-analysis-style summaries for object lifetime and boundary crossing.
4. Add persistent indexing so large workspaces do not require a full rebuild on every refresh.
