using Cyclotron.Core.Analysis;
using Cyclotron.Core.Graph;
using Cyclotron.Server;

namespace Cyclotron.Tests;

public sealed class CodebaseAnalyzerTests
{
    private readonly CodebaseAnalyzer _analyzer = new();

    [Fact]
    public async Task AnalyzeAsync_FindsInheritanceHierarchy()
    {
        var snapshot = await _analyzer.AnalyzeAsync(GetSampleProjectPath());

        var hierarchyType = snapshot.Snapshot.Graph.Nodes.Single(node => node.QualifiedName == "SampleCodebase.Pricing.SeasonalPricingStrategy");
        var inherits = snapshot.Snapshot.Graph.GetOutgoing(hierarchyType.Id).Single(edge => edge.Kind == Cyclotron.Core.Graph.CodeEdgeKind.Inherits);

        Assert.Equal("SampleCodebase.Pricing.PricingStrategyBase", snapshot.Snapshot.Graph.Nodes.Single(node => node.Id == inherits.ToId).QualifiedName);
    }

    [Fact]
    public async Task AnalyzeAsync_ComputesMetricsForOrderService()
    {
        var snapshot = await _analyzer.AnalyzeAsync(GetSampleProjectPath());
        var metrics = snapshot.Snapshot.TypeMetrics.Single(metric => metric.QualifiedName == "SampleCodebase.Orders.OrderService");

        Assert.True(metrics.CyclomaticComplexity >= 5);
        Assert.True(metrics.EfferentCoupling >= 2);
        Assert.True(metrics.CohesionScore >= 0 && metrics.CohesionScore <= 1);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsCycleRegion()
    {
        var snapshot = await _analyzer.AnalyzeAsync(GetSampleProjectPath());
        var cycle = snapshot.Snapshot.Signals.CycleRegions.Single(region =>
            region.SymbolNames.Contains("RecommendationService") &&
            region.SymbolNames.Contains("CatalogCache"));

        Assert.True(cycle.SymbolIds.Count >= 2);
    }

    [Fact]
    public async Task AnalyzeAsync_TracksAccessorBodies_WithoutCountingLocalFunctionsInOuterMethods()
    {
        var root = CreateTempDirectory();

        try
        {
            await WriteFileAsync(Path.Combine(root, "Sample.cs"), """
                public sealed class Sample
                {
                    private int _count;

                    public int Count
                    {
                        get
                        {
                            if (_count > 0)
                            {
                                return _count;
                            }

                            return 0;
                        }
                        set
                        {
                            if (value > 10)
                            {
                                _count = value;
                            }
                            else
                            {
                                _count = 0;
                            }
                        }
                    }

                    public int Compute(bool flag)
                    {
                        int Local(bool inner)
                        {
                            if (inner)
                            {
                                return 1;
                            }

                            return 0;
                        }

                        if (flag)
                        {
                            return 2;
                        }

                        return 3;
                    }
                }
                """);

            var snapshot = await _analyzer.AnalyzeAsync(root);
            var graph = snapshot.Snapshot.Graph;

            var propertyMetric = snapshot.Snapshot.MemberMetrics.Single(metric => metric.MemberKind == "Property");
            Assert.True(propertyMetric.CyclomaticComplexity >= 4);

            var propertyNode = graph.Nodes.Single(node =>
                node.Kind == CodeNodeKind.Member &&
                node.Metadata is not null &&
                node.Metadata.TryGetValue("symbolKind", out var symbolKind) &&
                symbolKind == "Property");
            var fieldNode = graph.Nodes.Single(node =>
                node.Kind == CodeNodeKind.Member &&
                node.Metadata is not null &&
                node.Metadata.TryGetValue("symbolKind", out var symbolKind) &&
                symbolKind == "Field");
            Assert.Contains(graph.GetOutgoing(propertyNode.Id), edge => edge.Kind == CodeEdgeKind.References && edge.ToId == fieldNode.Id);

            var computeMetric = snapshot.Snapshot.MemberMetrics.Single(metric => metric.MemberKind == "Ordinary");
            Assert.Equal(2, computeMetric.CyclomaticComplexity);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ResolveSymbol_WarnsWhenSimpleNameMatchesMultipleSymbols()
    {
        var root = CreateTempDirectory();

        try
        {
            await WriteFileAsync(Path.Combine(root, "A.cs"), "namespace First; public sealed class Duplicate { }");
            await WriteFileAsync(Path.Combine(root, "B.cs"), "namespace Second; public sealed class Duplicate { }");

            var service = new AnalysisWorkspaceService(_analyzer);
            var snapshot = await service.GetSnapshotAsync(root, forceRefresh: false, CancellationToken.None);
            var resolution = service.ResolveSymbol(snapshot, "Duplicate", CodeNodeKind.Type);

            Assert.NotNull(resolution.Selected);
            Assert.Equal(2, resolution.Candidates.Count(candidate => candidate.Name == "Duplicate"));
            Assert.NotNull(resolution.Message);
            Assert.Contains("matched multiple symbols", resolution.Message);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task FindUsagesAsync_UsesTheOwningSolutionForDirectoryProjectScans()
    {
        var root = CreateTempDirectory();

        try
        {
            await WriteProjectAsync(root, "ProjA", "Worker.cs", """
                namespace ProjA;

                public sealed class Worker
                {
                    public void Use() => Do();

                    public void Do()
                    {
                    }
                }
                """);
            await WriteProjectAsync(root, "ProjB", "Other.cs", """
                namespace ProjB;

                public sealed class Other
                {
                }
                """);

            var service = new AnalysisWorkspaceService(_analyzer);
            var snapshot = await service.GetSnapshotAsync(root, forceRefresh: false, CancellationToken.None);
            var resolution = service.ResolveSymbol(snapshot, "ProjA.Worker.Do()", CodeNodeKind.Member);

            var usages = await service.FindUsagesAsync(snapshot, resolution.Selected!.SymbolId, CancellationToken.None);

            Assert.Contains(usages, usage => Path.GetFileName(usage.FilePath) == "Worker.cs");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Tools_ReturnRelativePaths()
    {
        var service = new AnalysisWorkspaceService(_analyzer);
        var tools = new CodeGraphTools(service);
        var targetPath = GetSampleProjectPath();

        var analysis = await tools.AnalyzeCodebase(targetPath, cancellationToken: CancellationToken.None);
        var search = await tools.SearchSymbols(targetPath, "OrderService", cancellationToken: CancellationToken.None);
        var usages = await tools.FindSymbolUsages(targetPath, "RecommendationService", cancellationToken: CancellationToken.None);

        Assert.False(Path.IsPathRooted(analysis.TargetPath));
        Assert.Equal("SampleCodebase.csproj", analysis.TargetPath);
        Assert.All(search.Matches, match => Assert.True(match.FilePath is null || !Path.IsPathRooted(match.FilePath)));
        Assert.All(usages.Usages, usage => Assert.False(Path.IsPathRooted(usage.FilePath)));
    }

    [Fact]
    public async Task Tools_ReturnPathsRelativeToAnalysisRoot_WhenGivenSolutionDirectory()
    {
        var service = new AnalysisWorkspaceService(_analyzer);
        var tools = new CodeGraphTools(service);
        var targetPath = Path.GetDirectoryName(GetSampleProjectPath())!;

        var analysis = await tools.AnalyzeCodebase(targetPath, cancellationToken: CancellationToken.None);
        var search = await tools.SearchSymbols(targetPath, "OrderService", cancellationToken: CancellationToken.None);

        Assert.Equal(".", analysis.TargetPath);
        Assert.Contains(search.Matches, match => match.FilePath == Path.Combine("Orders", "OrderService.cs"));
    }

    [Fact]
    public async Task Service_ReusesSnapshotUntilSourcesChange()
    {
        var root = CreateTempDirectory();

        try
        {
            await WriteProjectAsync(root, "ProjA", "Worker.cs", """
                namespace ProjA;

                public sealed class Worker
                {
                    public int Value() => 1;
                }
                """);

            var service = new AnalysisWorkspaceService(_analyzer);

            var first = await service.GetSnapshotAsync(root, forceRefresh: false, CancellationToken.None);
            var second = await service.GetSnapshotAsync(root, forceRefresh: false, CancellationToken.None);

            Assert.Same(first, second);

            await Task.Delay(1100);
            await WriteFileAsync(Path.Combine(root, "ProjA", "Worker.cs"), """
                namespace ProjA;

                public sealed class Worker
                {
                    public int Value()
                    {
                        if (true)
                        {
                            return 1;
                        }

                        return 0;
                    }
                }
                """);

            var third = await service.GetSnapshotAsync(root, forceRefresh: false, CancellationToken.None);

            Assert.NotSame(first, third);
            Assert.Contains(third.Snapshot.MemberMetrics, metric => metric.CyclomaticComplexity > 1);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string GetSampleProjectPath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "samples", "SampleCodebase", "SampleCodebase.csproj");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cyclotron-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task WriteProjectAsync(string root, string projectName, string codeFileName, string code)
    {
        var projectDirectory = Path.Combine(root, projectName);
        Directory.CreateDirectory(projectDirectory);

        await WriteFileAsync(Path.Combine(projectDirectory, $"{projectName}.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await WriteFileAsync(Path.Combine(projectDirectory, codeFileName), code);
    }

    private static async Task WriteFileAsync(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content.Replace("\r\n", "\n"));
    }
}
