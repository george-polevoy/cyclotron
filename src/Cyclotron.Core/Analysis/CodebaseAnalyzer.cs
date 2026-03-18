using System.Collections.Concurrent;
using Cyclotron.Core.Graph;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Cyclotron.Core.Analysis;

public sealed class CodebaseAnalyzer
{
    private static readonly SymbolDisplayFormat QualifiedNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static int _msbuildRegistered;

    public async Task<CodeWorkspaceSnapshot> AnalyzeAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        var loadedWorkspace = await LoadWorkspaceAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var state = new BuilderState(loadedWorkspace.ResolvedPath);

        foreach (var project in loadedWorkspace.Projects.OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectId = $"project:{Path.GetFullPath(project.FilePath ?? project.Name)}";
            state.AddNode(new CodeGraphNode(
                projectId,
                CodeNodeKind.Project,
                project.Name,
                project.FilePath ?? project.Name,
                project.FilePath,
                new Dictionary<string, string?>
                {
                    ["language"] = project.Language,
                }));
            state.AddEdge(state.SolutionNodeId, projectId, CodeEdgeKind.Contains);

            foreach (var document in project.Documents.Where(IsAnalyzableDocument))
            {
                var filePath = Path.GetFullPath(document.FilePath!);
                var fileId = $"file:{filePath}";
                state.FileNodeIds[filePath] = fileId;
                state.AddNode(new CodeGraphNode(
                    fileId,
                    CodeNodeKind.File,
                    Path.GetFileName(filePath),
                    filePath,
                    filePath,
                    new Dictionary<string, string?>
                    {
                        ["project"] = project.Name,
                    }));
                state.AddEdge(projectId, fileId, CodeEdgeKind.Contains);
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                state.Diagnostics.Add($"Unable to compile project '{project.Name}'.");
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken)
                         .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                         .Take(25))
            {
                state.Diagnostics.Add($"{project.Name}: {diagnostic}");
            }

            AddProjectSymbols(project, compilation, projectId, state, cancellationToken);
            await AnalyzeProjectBodiesAsync(project, state, cancellationToken).ConfigureAwait(false);
        }

        state.Diagnostics.AddRange(loadedWorkspace.Diagnostics);

        var typeMetrics = BuildTypeMetrics(state);
        var qualitySignals = BuildGraphSignals(state, typeMetrics);
        var graph = new CodeGraph(state.Nodes.Values, state.Edges.Select(edge => new CodeGraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Label)));

        var snapshot = new CodebaseSnapshot(
            loadedWorkspace.ResolvedPath,
            DateTimeOffset.UtcNow,
            graph,
            state.MemberMetrics.Values.OrderBy(metric => metric.QualifiedName, StringComparer.Ordinal).ToArray(),
            typeMetrics.OrderBy(metric => metric.QualifiedName, StringComparer.Ordinal).ToArray(),
            qualitySignals,
            state.Diagnostics.Distinct(StringComparer.Ordinal).OrderBy(message => message, StringComparer.Ordinal).ToArray());

        return new CodeWorkspaceSnapshot(
            snapshot,
            loadedWorkspace.Solution,
            new Dictionary<string, ISymbol>(state.SymbolsById, StringComparer.Ordinal),
            new Dictionary<string, Solution>(state.SolutionsBySymbolId, StringComparer.Ordinal));
    }

    private static async Task<LoadedWorkspace> LoadWorkspaceAsync(string targetPath, CancellationToken cancellationToken)
    {
        var resolvedPath = ResolveInputPath(targetPath);
        if (File.Exists(resolvedPath))
        {
            return await LoadFromPathAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        }

        var solutionFiles = Directory.GetFiles(resolvedPath, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (solutionFiles.Length > 0)
        {
            return await LoadFromPathAsync(solutionFiles[0], cancellationToken).ConfigureAwait(false);
        }

        var projectFiles = Directory.GetFiles(resolvedPath, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !IsInBuildOutput(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (projectFiles.Length > 0)
        {
            EnsureMsBuildRegistered();
            using var workspace = MSBuildWorkspace.Create();
            var diagnostics = new ConcurrentBag<string>();
            workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Add(args.Diagnostic.ToString()));

            var loadedProjects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
            foreach (var projectFile in projectFiles)
            {
                var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var solutionProject in project.Solution.Projects.Where(candidate => candidate.Language == LanguageNames.CSharp))
                {
                    var key = Path.GetFullPath(solutionProject.FilePath ?? solutionProject.Name);
                    loadedProjects[key] = solutionProject;
                }
            }

            return new LoadedWorkspace(
                resolvedPath,
                workspace.CurrentSolution,
                loadedProjects.Values.OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                diagnostics.OrderBy(message => message, StringComparer.Ordinal).ToArray());
        }

        return await LoadLooseFilesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LoadedWorkspace> LoadFromPathAsync(string path, CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();

        var diagnostics = new ConcurrentBag<string>();
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(args => diagnostics.Add(args.Diagnostic.ToString()));

        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LoadedWorkspace(
                Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory(),
                solution,
                solution.Projects.Where(project => project.Language == LanguageNames.CSharp).ToArray(),
                diagnostics.OrderBy(message => message, StringComparer.Ordinal).ToArray());
        }

        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LoadedWorkspace(
                Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory(),
                project.Solution,
                new[] { project },
                diagnostics.OrderBy(message => message, StringComparer.Ordinal).ToArray());
        }

        throw new InvalidOperationException($"Unsupported input path '{path}'. Expected a directory, .sln, or .csproj.");
    }

    private static async Task<LoadedWorkspace> LoadLooseFilesAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Cyclotron.Adhoc",
            "Cyclotron.Adhoc",
            LanguageNames.CSharp,
            filePath: Path.Combine(directoryPath, "Cyclotron.Adhoc.csproj"),
            metadataReferences: CreateMetadataReferences(),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        var project = workspace.AddProject(projectInfo);

        foreach (var file in Directory.GetFiles(directoryPath, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !IsInBuildOutput(file))
                     .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            var source = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var fullPath = Path.GetFullPath(file);
            var documentInfo = DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id),
                Path.GetFileName(file),
                filePath: fullPath,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), fullPath)));
            project = workspace.AddDocument(documentInfo).Project;
        }

        return new LoadedWorkspace(
            directoryPath,
            project.Solution,
            new[] { project },
            Array.Empty<string>());
    }

    private static void AddProjectSymbols(
        Project project,
        Compilation compilation,
        string projectId,
        BuilderState state,
        CancellationToken cancellationToken)
    {
        foreach (var member in compilation.Assembly.GlobalNamespace.GetMembers())
        {
            if (member is INamespaceSymbol namespaceSymbol)
            {
                AddNamespaceSymbol(project, namespaceSymbol, projectId, state, cancellationToken);
            }
            else if (member is INamedTypeSymbol typeSymbol)
            {
                AddTypeSymbol(project, typeSymbol, projectId, state, cancellationToken);
            }
        }
    }

    private static void AddNamespaceSymbol(
        Project project,
        INamespaceSymbol namespaceSymbol,
        string parentId,
        BuilderState state,
        CancellationToken cancellationToken)
    {
        if (namespaceSymbol.IsGlobalNamespace)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (member is INamespaceSymbol nestedNamespace)
                {
                    AddNamespaceSymbol(project, nestedNamespace, parentId, state, cancellationToken);
                }
                else if (member is INamedTypeSymbol nestedType)
                {
                    AddTypeSymbol(project, nestedType, parentId, state, cancellationToken);
                }
            }

            return;
        }

        var namespaceId = AddSymbolNode(project, namespaceSymbol, CodeNodeKind.Namespace, parentId, state);

        foreach (var member in namespaceSymbol.GetMembers())
        {
            if (member is INamespaceSymbol nestedNamespace)
            {
                AddNamespaceSymbol(project, nestedNamespace, namespaceId, state, cancellationToken);
            }
            else if (member is INamedTypeSymbol nestedType)
            {
                AddTypeSymbol(project, nestedType, namespaceId, state, cancellationToken);
            }
        }
    }

    private static void AddTypeSymbol(
        Project project,
        INamedTypeSymbol typeSymbol,
        string parentId,
        BuilderState state,
        CancellationToken cancellationToken)
    {
        if (!IsSourceSymbol(typeSymbol))
        {
            return;
        }

        var typeId = AddSymbolNode(project, typeSymbol, CodeNodeKind.Type, parentId, state);
        state.TypeKinds[typeId] = typeSymbol.TypeKind.ToString();

        if (typeSymbol.BaseType is not null && typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            AddNamedTypeDependency(typeId, typeSymbol.BaseType, CodeEdgeKind.Inherits, state);
        }

        foreach (var implementedInterface in typeSymbol.Interfaces)
        {
            AddNamedTypeDependency(typeId, implementedInterface, CodeEdgeKind.Implements, state);
        }

        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            AddTypeSymbol(project, nestedType, typeId, state, cancellationToken);
        }

        foreach (var member in typeSymbol.GetMembers().Where(ShouldModelMember))
        {
            AddMemberSymbol(project, member, typeId, state);
        }
    }

    private static void AddMemberSymbol(Project project, ISymbol member, string parentTypeId, BuilderState state)
    {
        var memberId = AddSymbolNode(project, member, CodeNodeKind.Member, parentTypeId, state);
        state.MemberToContainingType[memberId] = parentTypeId;
        state.MemberKinds[memberId] = GetMemberKind(member);

        if (member is IMethodSymbol method && IsMethodWithBody(method))
        {
            state.TypeMethodIds.GetOrAdd(parentTypeId, _ => new HashSet<string>(StringComparer.Ordinal)).Add(memberId);
        }

        foreach (var dependency in CollectSignatureTypes(member))
        {
            AddMemberTypeDependency(memberId, parentTypeId, dependency, state);
        }
    }

    private static async Task AnalyzeProjectBodiesAsync(Project project, BuilderState state, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents.Where(IsAnalyzableDocument))
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null || syntaxRoot is null)
            {
                continue;
            }

            foreach (var declaration in syntaxRoot.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                if (symbol is not IMethodSymbol methodSymbol || !IsMethodWithBody(methodSymbol))
                {
                    continue;
                }

                RegisterMethodBodyAnalysis(semanticModel, declaration, methodSymbol, state, cancellationToken);
            }

            foreach (var accessor in syntaxRoot.DescendantNodes().OfType<AccessorDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var accessorSymbol = semanticModel.GetDeclaredSymbol(accessor, cancellationToken);
                if (accessorSymbol?.AssociatedSymbol is null)
                {
                    continue;
                }

                RegisterMemberBodyAnalysis(
                    semanticModel,
                    accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody?.Expression,
                    accessorSymbol.AssociatedSymbol,
                    accessor.SyntaxTree.FilePath,
                    state,
                    cancellationToken);
            }

            foreach (var property in syntaxRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (property.ExpressionBody is null)
                {
                    continue;
                }

                var propertySymbol = semanticModel.GetDeclaredSymbol(property, cancellationToken);
                if (propertySymbol is null)
                {
                    continue;
                }

                RegisterMemberBodyAnalysis(
                    semanticModel,
                    property.ExpressionBody.Expression,
                    propertySymbol,
                    property.SyntaxTree.FilePath,
                    state,
                    cancellationToken);
            }

            foreach (var indexer in syntaxRoot.DescendantNodes().OfType<IndexerDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (indexer.ExpressionBody is null)
                {
                    continue;
                }

                var indexerSymbol = semanticModel.GetDeclaredSymbol(indexer, cancellationToken);
                if (indexerSymbol is null)
                {
                    continue;
                }

                RegisterMemberBodyAnalysis(
                    semanticModel,
                    indexer.ExpressionBody.Expression,
                    indexerSymbol,
                    indexer.SyntaxTree.FilePath,
                    state,
                    cancellationToken);
            }
        }
    }

    private static void RegisterMethodBodyAnalysis(
        SemanticModel semanticModel,
        BaseMethodDeclarationSyntax declaration,
        IMethodSymbol methodSymbol,
        BuilderState state,
        CancellationToken cancellationToken)
    {
        RegisterMemberBodyAnalysis(
            semanticModel,
            GetBodyRoot(declaration),
            methodSymbol,
            declaration.SyntaxTree.FilePath,
            state,
            cancellationToken);
    }

    private static void RegisterMemberBodyAnalysis(
        SemanticModel semanticModel,
        SyntaxNode? bodyRoot,
        ISymbol memberSymbol,
        string? filePath,
        BuilderState state,
        CancellationToken cancellationToken)
    {
        var memberId = CreateSymbolId(memberSymbol);
        if (!state.MemberToContainingType.TryGetValue(memberId, out var containingTypeId))
        {
            return;
        }

        var complexity = CyclomaticComplexityWalker.Calculate(bodyRoot);
        var normalizedFilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        if (state.MemberMetrics.TryGetValue(memberId, out var existingMetric))
        {
            state.MemberMetrics[memberId] = existingMetric with
            {
                CyclomaticComplexity = existingMetric.CyclomaticComplexity + complexity,
                FilePath = existingMetric.FilePath ?? normalizedFilePath,
            };
        }
        else
        {
            state.MemberMetrics[memberId] = new MemberMetric(
                memberId,
                GetDisplayName(memberSymbol),
                GetQualifiedName(memberSymbol),
                GetMemberKind(memberSymbol),
                containingTypeId,
                complexity,
                normalizedFilePath);
        }

        if (bodyRoot is null)
        {
            return;
        }

        var fieldUsage = state.MethodFieldUsage.GetOrAdd(memberId, _ => new HashSet<string>(StringComparer.Ordinal));
        var bodyNodes = EnumerateBodyNodes(bodyRoot).ToArray();

        foreach (var invocation in bodyNodes.OfType<InvocationExpressionSyntax>())
        {
            var target = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            RegisterMethodTarget(state, memberId, containingTypeId, target, CodeEdgeKind.Calls);
        }

        foreach (var creation in bodyNodes.OfType<ObjectCreationExpressionSyntax>())
        {
            var constructor = semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol;
            RegisterMethodTarget(state, memberId, containingTypeId, constructor, CodeEdgeKind.Calls);

            var createdType = semanticModel.GetTypeInfo(creation, cancellationToken).Type;
            foreach (var namedType in CollectNamedTypes(createdType))
            {
                AddMemberTypeDependency(memberId, containingTypeId, namedType, state);
            }
        }

        foreach (var typeSyntax in bodyNodes.OfType<TypeSyntax>())
        {
            var referencedType = semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type;
            foreach (var namedType in CollectNamedTypes(referencedType))
            {
                AddMemberTypeDependency(memberId, containingTypeId, namedType, state);
            }
        }

        foreach (var simpleName in bodyNodes.OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(simpleName, cancellationToken).Symbol;
            if (symbol is IFieldSymbol field)
            {
                var fieldId = CreateSymbolId(field);
                if (state.SymbolsById.ContainsKey(fieldId))
                {
                    state.AddEdge(memberId, fieldId, CodeEdgeKind.References);
                }

                if (field.ContainingType is not null &&
                    string.Equals(CreateSymbolId(field.ContainingType), containingTypeId, StringComparison.Ordinal))
                {
                    fieldUsage.Add(fieldId);
                }

                if (field.ContainingType is not null)
                {
                    AddContainingTypeDependency(containingTypeId, field.ContainingType, state);
                }
            }
            else if (symbol is IPropertySymbol property)
            {
                var propertyId = CreateSymbolId(property);
                if (state.SymbolsById.ContainsKey(propertyId))
                {
                    state.AddEdge(memberId, propertyId, CodeEdgeKind.References);
                }

                if (property.ContainingType is not null)
                {
                    AddContainingTypeDependency(containingTypeId, property.ContainingType, state);
                }
            }
            else if (symbol is IEventSymbol eventSymbol)
            {
                var eventId = CreateSymbolId(eventSymbol);
                if (state.SymbolsById.ContainsKey(eventId))
                {
                    state.AddEdge(memberId, eventId, CodeEdgeKind.References);
                }

                if (eventSymbol.ContainingType is not null)
                {
                    AddContainingTypeDependency(containingTypeId, eventSymbol.ContainingType, state);
                }
            }
        }
    }

    private static IEnumerable<SyntaxNode> EnumerateBodyNodes(SyntaxNode bodyRoot)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(bodyRoot);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (current is LocalFunctionStatementSyntax)
            {
                continue;
            }

            var children = current.ChildNodes().ToArray();
            for (var index = children.Length - 1; index >= 0; index--)
            {
                stack.Push(children[index]);
            }
        }
    }

    private static IReadOnlyList<TypeMetric> BuildTypeMetrics(BuilderState state)
    {
        var allTypeIds = state.SymbolsById
            .Where(entry => entry.Value is INamedTypeSymbol)
            .Select(entry => entry.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var afferentCoupling = allTypeIds.ToDictionary(typeId => typeId, _ => 0, StringComparer.Ordinal);
        foreach (var dependency in state.TypeDependencies)
        {
            foreach (var target in dependency.Value)
            {
                if (afferentCoupling.ContainsKey(target))
                {
                    afferentCoupling[target]++;
                }
            }
        }

        var metrics = new List<TypeMetric>(allTypeIds.Length);
        foreach (var typeId in allTypeIds)
        {
            var symbol = (INamedTypeSymbol)state.SymbolsById[typeId];
            var methodIds = state.TypeMethodIds.TryGetValue(typeId, out var knownMethods)
                ? knownMethods.Where(state.MemberMetrics.ContainsKey).ToArray()
                : Array.Empty<string>();

            var methodMetrics = methodIds.Select(methodId => state.MemberMetrics[methodId]).ToArray();
            var sharedFieldPairs = 0;
            var totalPairs = 0;
            for (var index = 0; index < methodIds.Length; index++)
            {
                for (var other = index + 1; other < methodIds.Length; other++)
                {
                    totalPairs++;
                    IEnumerable<string> leftFields = state.MethodFieldUsage.TryGetValue(methodIds[index], out var left)
                        ? left
                        : Array.Empty<string>();
                    IEnumerable<string> rightFields = state.MethodFieldUsage.TryGetValue(methodIds[other], out var right)
                        ? right
                        : Array.Empty<string>();

                    if (leftFields.Intersect(rightFields, StringComparer.Ordinal).Any())
                    {
                        sharedFieldPairs++;
                    }
                }
            }

            var cohesion = methodIds.Length <= 1
                ? 1d
                : totalPairs == 0
                    ? 0d
                    : (double)sharedFieldPairs / totalPairs;

            var internalOutgoing = state.TypeDependencies.TryGetValue(typeId, out var outgoing)
                ? outgoing.Count
                : 0;
            var externalOutgoing = state.ExternalTypeDependencies.TryGetValue(typeId, out var external)
                ? external.Count
                : 0;
            var efferent = internalOutgoing + externalOutgoing;
            var afferent = afferentCoupling[typeId];
            var instability = afferent + efferent == 0 ? 0d : (double)efferent / (afferent + efferent);

            metrics.Add(new TypeMetric(
                typeId,
                GetDisplayName(symbol),
                GetQualifiedName(symbol),
                symbol.TypeKind.ToString(),
                methodMetrics.Length,
                methodMetrics.Sum(metric => metric.CyclomaticComplexity),
                methodMetrics.DefaultIfEmpty().Max(metric => metric?.CyclomaticComplexity ?? 0),
                cohesion,
                afferent,
                internalOutgoing,
                externalOutgoing,
                instability,
                GetPrimarySourcePath(symbol)));
        }

        return metrics;
    }

    private static GraphQualitySignals BuildGraphSignals(BuilderState state, IReadOnlyList<TypeMetric> typeMetrics)
    {
        var typeIds = typeMetrics.Select(metric => metric.SymbolId).ToArray();
        var adjacency = typeIds.ToDictionary(
            typeId => typeId,
            typeId => state.TypeDependencies.TryGetValue(typeId, out var outgoing)
                ? new HashSet<string>(outgoing, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var stronglyConnectedComponents = GraphAlgorithms.FindStronglyConnectedComponents(adjacency)
            .Where(component => component.Count > 1 || adjacency[component[0]].Contains(component[0]))
            .OrderByDescending(component => component.Count)
            .ToArray();

        var undirectedAdjacency = typeIds.ToDictionary(
            typeId => typeId,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (source, targets) in adjacency)
        {
            foreach (var target in targets)
            {
                if (!undirectedAdjacency.ContainsKey(target))
                {
                    continue;
                }

                undirectedAdjacency[source].Add(target);
                undirectedAdjacency[target].Add(source);
            }
        }

        var betweenness = GraphAlgorithms.ComputeBetweennessCentrality(undirectedAdjacency);
        var maxComplexity = Math.Max(1, typeMetrics.Max(metric => metric.CyclomaticComplexity));
        var maxCoupling = Math.Max(1, typeMetrics.Max(metric => metric.AfferentCoupling + metric.EfferentCoupling + metric.ExternalCoupling));
        var maxBetweenness = Math.Max(1d, betweenness.Values.DefaultIfEmpty(0d).Max());
        var cycleTypeIds = stronglyConnectedComponents.SelectMany(component => component).ToHashSet(StringComparer.Ordinal);

        var hotspots = typeMetrics
            .Select(metric =>
            {
                var totalCoupling = metric.AfferentCoupling + metric.EfferentCoupling + metric.ExternalCoupling;
                var normalizedComplexity = metric.CyclomaticComplexity / (double)maxComplexity;
                var normalizedCoupling = totalCoupling / (double)maxCoupling;
                var normalizedBrokerage = betweenness.TryGetValue(metric.SymbolId, out var score) ? score / maxBetweenness : 0d;
                var inCycle = cycleTypeIds.Contains(metric.SymbolId);
                var riskScore = (0.35d * normalizedComplexity)
                    + (0.25d * normalizedCoupling)
                    + (0.15d * normalizedBrokerage)
                    + (0.15d * (1d - metric.CohesionScore))
                    + (0.10d * (inCycle ? 1d : 0d));

                return new GraphHotspot(
                    metric.SymbolId,
                    metric.Name,
                    metric.QualifiedName,
                    Math.Round(riskScore, 4),
                    metric.CyclomaticComplexity,
                    totalCoupling,
                    Math.Round(metric.CohesionScore, 4),
                    Math.Round(metric.Instability, 4),
                    Math.Round(normalizedBrokerage, 4),
                    inCycle);
            })
            .OrderByDescending(hotspot => hotspot.RiskScore)
            .ThenBy(hotspot => hotspot.QualifiedName, StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        var metricLookup = typeMetrics.ToDictionary(metric => metric.SymbolId, StringComparer.Ordinal);
        var cycleRegions = stronglyConnectedComponents
            .Select(component =>
            {
                var componentMetrics = component
                    .Where(metricLookup.ContainsKey)
                    .Select(typeId => metricLookup[typeId])
                    .ToArray();

                return new GraphCycleRegion(
                    component,
                    componentMetrics.Select(metric => metric.Name).ToArray(),
                    Math.Round(componentMetrics.DefaultIfEmpty().Average(metric => metric?.CyclomaticComplexity ?? 0), 2),
                    Math.Round(componentMetrics.DefaultIfEmpty().Average(metric => metric is null
                        ? 0
                        : metric.AfferentCoupling + metric.EfferentCoupling + metric.ExternalCoupling), 2));
            })
            .OrderByDescending(region => region.SymbolIds.Count)
            .ThenByDescending(region => region.AverageComplexity)
            .Take(10)
            .ToArray();

        var brokers = typeMetrics
            .Select(metric => new GraphBroker(
                metric.SymbolId,
                metric.Name,
                metric.QualifiedName,
                Math.Round(betweenness.TryGetValue(metric.SymbolId, out var value) ? value / maxBetweenness : 0d, 4),
                undirectedAdjacency.TryGetValue(metric.SymbolId, out var neighbors) ? neighbors.Count : 0))
            .OrderByDescending(broker => broker.BetweennessCentrality)
            .ThenByDescending(broker => broker.NeighborCount)
            .Take(10)
            .ToArray();

        var edgeCount = adjacency.Sum(entry => entry.Value.Count);
        var density = typeIds.Length <= 1
            ? 0d
            : edgeCount / (double)(typeIds.Length * (typeIds.Length - 1));

        return new GraphQualitySignals(
            new GraphOverview(
                state.Nodes.Count,
                state.Edges.Count,
                typeIds.Length,
                state.MemberMetrics.Count,
                cycleRegions.Length,
                Math.Round(density, 4)),
            hotspots,
            cycleRegions,
            brokers);
    }

    private static void RegisterMethodTarget(
        BuilderState state,
        string sourceMemberId,
        string containingTypeId,
        IMethodSymbol? target,
        CodeEdgeKind edgeKind)
    {
        if (target is null)
        {
            return;
        }

        if (target.ReducedFrom is not null)
        {
            target = target.ReducedFrom;
        }

        var targetId = CreateSymbolId(target);
        if (state.SymbolsById.ContainsKey(targetId))
        {
            state.AddEdge(sourceMemberId, targetId, edgeKind);
        }

        if (target.ContainingType is not null)
        {
            AddContainingTypeDependency(containingTypeId, target.ContainingType, state);
        }
    }

    private static void AddMemberTypeDependency(
        string memberId,
        string containingTypeId,
        INamedTypeSymbol dependency,
        BuilderState state)
    {
        AddContainingTypeDependency(containingTypeId, dependency, state);

        var dependencyId = CreateSymbolId(dependency);
        if (state.SymbolsById.ContainsKey(dependencyId))
        {
            state.AddEdge(memberId, dependencyId, CodeEdgeKind.UsesType);
        }
    }

    private static void AddContainingTypeDependency(string sourceTypeId, INamedTypeSymbol dependency, BuilderState state)
    {
        var dependencyId = CreateSymbolId(dependency);
        if (string.Equals(sourceTypeId, dependencyId, StringComparison.Ordinal))
        {
            return;
        }

        if (state.SymbolsById.ContainsKey(dependencyId))
        {
            state.TypeDependencies.GetOrAdd(sourceTypeId, _ => new HashSet<string>(StringComparer.Ordinal)).Add(dependencyId);
        }
        else
        {
            state.ExternalTypeDependencies
                .GetOrAdd(sourceTypeId, _ => new HashSet<string>(StringComparer.Ordinal))
                .Add(GetQualifiedName(dependency));
        }
    }

    private static void AddNamedTypeDependency(string sourceTypeId, INamedTypeSymbol dependency, CodeEdgeKind edgeKind, BuilderState state)
    {
        var dependencyId = CreateSymbolId(dependency);
        if (!state.SymbolsById.ContainsKey(dependencyId))
        {
            return;
        }

        state.AddEdge(sourceTypeId, dependencyId, edgeKind);
        state.TypeDependencies.GetOrAdd(sourceTypeId, _ => new HashSet<string>(StringComparer.Ordinal)).Add(dependencyId);
    }

    private static string AddSymbolNode(Project project, ISymbol symbol, CodeNodeKind kind, string parentId, BuilderState state)
    {
        var symbolId = CreateSymbolId(symbol);
        state.SymbolsById.TryAdd(symbolId, symbol);
        state.SolutionsBySymbolId.TryAdd(symbolId, project.Solution);

        var metadata = new Dictionary<string, string?>
        {
            ["project"] = project.Name,
            ["symbolKind"] = symbol.Kind.ToString(),
        };

        if (symbol is INamedTypeSymbol typeSymbol)
        {
            metadata["typeKind"] = typeSymbol.TypeKind.ToString();
        }

        if (symbol is not INamespaceSymbol && symbol.ContainingType is not null)
        {
            metadata["containingTypeId"] = CreateSymbolId(symbol.ContainingType);
        }

        state.AddNode(new CodeGraphNode(
            symbolId,
            kind,
            GetDisplayName(symbol),
            GetQualifiedName(symbol),
            GetPrimarySourcePath(symbol),
            metadata));
        state.AddEdge(parentId, symbolId, CodeEdgeKind.Contains);

        foreach (var location in symbol.Locations.Where(location => location.IsInSource && !string.IsNullOrWhiteSpace(location.SourceTree?.FilePath)))
        {
            var filePath = Path.GetFullPath(location.SourceTree!.FilePath);
            if (!state.FileNodeIds.TryGetValue(filePath, out var fileId))
            {
                continue;
            }

            state.AddEdge(symbolId, fileId, CodeEdgeKind.DeclaredIn);
        }

        return symbolId;
    }

    private static SyntaxNode? GetBodyRoot(BaseMethodDeclarationSyntax declaration) =>
        declaration switch
        {
            MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression,
            ConstructorDeclarationSyntax constructor => (SyntaxNode?)constructor.Body ?? constructor.ExpressionBody?.Expression,
            DestructorDeclarationSyntax destructor => (SyntaxNode?)destructor.Body ?? destructor.ExpressionBody?.Expression,
            OperatorDeclarationSyntax operatorDeclaration => (SyntaxNode?)operatorDeclaration.Body ?? operatorDeclaration.ExpressionBody?.Expression,
            ConversionOperatorDeclarationSyntax conversion => (SyntaxNode?)conversion.Body ?? conversion.ExpressionBody?.Expression,
            _ => null,
        };

    private static bool IsAnalyzableDocument(Document document) =>
        document.SupportsSyntaxTree &&
        string.Equals(document.Project.Language, LanguageNames.CSharp, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(document.FilePath);

    private static string ResolveInputPath(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Target path '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static bool ShouldModelMember(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => IsMethodWithBody(method) || method.MethodKind == MethodKind.Constructor,
            IPropertySymbol => true,
            IFieldSymbol field => !field.IsImplicitlyDeclared,
            IEventSymbol => true,
            _ => false,
        };

    private static bool IsMethodWithBody(IMethodSymbol method) =>
        !method.IsImplicitlyDeclared &&
        method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor
            or MethodKind.UserDefinedOperator or MethodKind.Conversion;

    private static string GetPrimarySourcePath(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource && !string.IsNullOrWhiteSpace(candidate.SourceTree?.FilePath));
        return string.IsNullOrWhiteSpace(location?.SourceTree?.FilePath)
            ? string.Empty
            : Path.GetFullPath(location.SourceTree!.FilePath);
    }

    private static string CreateSymbolId(ISymbol symbol) =>
        symbol.GetDocumentationCommentId()
        ?? $"{symbol.Kind}:{symbol.ContainingAssembly?.Name}:{GetQualifiedName(symbol)}";

    private static string GetQualifiedName(ISymbol symbol) =>
        symbol.ToDisplayString(QualifiedNameFormat);

    private static string GetDisplayName(ISymbol symbol) =>
        symbol switch
        {
            INamespaceSymbol namespaceSymbol => namespaceSymbol.ToDisplayString(),
            _ => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        };

    private static string GetMemberKind(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.MethodKind.ToString(),
            IPropertySymbol => "Property",
            IFieldSymbol => "Field",
            IEventSymbol => "Event",
            _ => symbol.Kind.ToString(),
        };

    private static bool IsSourceSymbol(ISymbol symbol) =>
        symbol.Locations.Any(location => location.IsInSource);

    private static IEnumerable<INamedTypeSymbol> CollectSignatureTypes(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => CollectNamedTypes(method.ReturnType)
                .Concat(method.Parameters.SelectMany(parameter => CollectNamedTypes(parameter.Type))),
            IPropertySymbol property => CollectNamedTypes(property.Type),
            IFieldSymbol field => CollectNamedTypes(field.Type),
            IEventSymbol eventSymbol => CollectNamedTypes(eventSymbol.Type),
            _ => Array.Empty<INamedTypeSymbol>(),
        };

    private static IEnumerable<INamedTypeSymbol> CollectNamedTypes(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            yield break;
        }

        switch (typeSymbol)
        {
            case INamedTypeSymbol namedType:
                yield return namedType.IsGenericType ? namedType.OriginalDefinition : namedType;
                foreach (var typeArgument in namedType.TypeArguments)
                {
                    foreach (var nested in CollectNamedTypes(typeArgument))
                    {
                        yield return nested;
                    }
                }

                if (namedType.ContainingType is not null)
                {
                    foreach (var nested in CollectNamedTypes(namedType.ContainingType))
                    {
                        yield return nested;
                    }
                }

                yield break;

            case IArrayTypeSymbol arrayType:
                foreach (var nested in CollectNamedTypes(arrayType.ElementType))
                {
                    yield return nested;
                }

                yield break;

            case IPointerTypeSymbol pointerType:
                foreach (var nested in CollectNamedTypes(pointerType.PointedAtType))
                {
                    yield return nested;
                }

                yield break;
        }
    }

    private static IEnumerable<MetadataReference> CreateMetadataReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        foreach (var assemblyPath in trustedAssemblies)
        {
            yield return MetadataReference.CreateFromFile(assemblyPath);
        }
    }

    private static bool IsInBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static void EnsureMsBuildRegistered()
    {
        if (Interlocked.Exchange(ref _msbuildRegistered, 1) == 1)
        {
            return;
        }

        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        var instances = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(instance => instance.Version).ToArray();
        if (instances.Length > 0)
        {
            MSBuildLocator.RegisterInstance(instances[0]);
            return;
        }

        MSBuildLocator.RegisterDefaults();
    }

    private sealed record LoadedWorkspace(
        string ResolvedPath,
        Solution Solution,
        IReadOnlyList<Project> Projects,
        IReadOnlyList<string> Diagnostics);

    private sealed class BuilderState
    {
        private readonly HashSet<EdgeKey> _edgeKeys = new();

        public BuilderState(string targetPath)
        {
            SolutionNodeId = $"solution:{targetPath}";
            AddNode(new CodeGraphNode(
                SolutionNodeId,
                CodeNodeKind.Solution,
                Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                targetPath,
                targetPath,
                new Dictionary<string, string?>()));
        }

        public Dictionary<string, CodeGraphNode> Nodes { get; } = new(StringComparer.Ordinal);

        public List<EdgeKey> Edges { get; } = new();

        public string SolutionNodeId { get; }

        public Dictionary<string, string> FileNodeIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ISymbol> SymbolsById { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Solution> SolutionsBySymbolId { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> MemberToContainingType { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> MemberKinds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> TypeKinds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, MemberMetric> MemberMetrics { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> DiagnosticsMap { get; } = new(StringComparer.Ordinal);

        public List<string> Diagnostics { get; } = new();

        public ConcurrentDictionary<string, HashSet<string>> TypeMethodIds { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, HashSet<string>> MethodFieldUsage { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, HashSet<string>> TypeDependencies { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, HashSet<string>> ExternalTypeDependencies { get; } = new(StringComparer.Ordinal);

        public void AddNode(CodeGraphNode node) => Nodes[node.Id] = node;

        public void AddEdge(string fromId, string toId, CodeEdgeKind kind, string? label = null)
        {
            var edge = new EdgeKey(fromId, toId, kind, label);
            if (_edgeKeys.Add(edge))
            {
                Edges.Add(edge);
            }
        }

        public sealed record EdgeKey(string FromId, string ToId, CodeEdgeKind Kind, string? Label);
    }
}
