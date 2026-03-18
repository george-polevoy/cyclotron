namespace SampleCodebase.Recommendations;

public sealed class CatalogCache
{
    private readonly RecommendationService _recommendationService;
    private readonly List<string> _catalog = new() { "A-100", "A-200", "B-300", "C-400" };

    public CatalogCache(RecommendationService recommendationService)
    {
        _recommendationService = recommendationService;
    }

    public IReadOnlyList<string> GetCatalogSnapshot() => _catalog;

    public IReadOnlyList<string> GetWarmupSkus() => _recommendationService
        .SeedSkus()
        .DefaultIfEmpty("A-100")
        .ToArray();
}
