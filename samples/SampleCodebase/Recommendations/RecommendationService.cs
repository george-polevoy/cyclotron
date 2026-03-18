using SampleCodebase.Domain;

namespace SampleCodebase.Recommendations;

public sealed class RecommendationService
{
    private readonly CatalogCache _catalogCache;

    public RecommendationService(CatalogCache catalogCache)
    {
        _catalogCache = catalogCache;
    }

    public IReadOnlyList<string> Recommend(Order order)
    {
        var catalog = _catalogCache.GetCatalogSnapshot();
        return catalog
            .Where(sku => order.Lines.All(line => line.Sku != sku))
            .Take(3)
            .ToArray();
    }

    public IReadOnlyList<string> SeedSkus() => _catalogCache.GetWarmupSkus();
}
