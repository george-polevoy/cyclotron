using SampleCodebase.Domain;
using SampleCodebase.Inventory;
using SampleCodebase.Pricing;
using SampleCodebase.Recommendations;

namespace SampleCodebase.Orders;

public sealed class OrderService
{
    private readonly InventoryGateway _inventoryGateway;
    private readonly IPricingStrategy _pricingStrategy;
    private readonly AuditTrail _auditTrail;
    private readonly RecommendationService _recommendationService;

    public OrderService(
        InventoryGateway inventoryGateway,
        IPricingStrategy pricingStrategy,
        AuditTrail auditTrail,
        RecommendationService recommendationService)
    {
        _inventoryGateway = inventoryGateway;
        _pricingStrategy = pricingStrategy;
        _auditTrail = auditTrail;
        _recommendationService = recommendationService;
    }

    public decimal PlaceOrder(Order order, CustomerTier tier, bool peakSeason)
    {
        var flaggedLines = new List<OrderLine>();
        foreach (var line in order.Lines)
        {
            if (!_inventoryGateway.HasCapacity(line))
            {
                throw new InvalidOperationException($"Insufficient inventory for {line.Sku}.");
            }

            if (_inventoryGateway.RequiresManualReview(line) || order.IsExpedited)
            {
                flaggedLines.Add(line);
            }
        }

        if (flaggedLines.Count > 0 && tier == CustomerTier.Standard)
        {
            _auditTrail.Record("Manual review required for standard customer.");
        }
        else if (flaggedLines.Count > 0)
        {
            _auditTrail.Record("Priority manual review requested.");
        }
        else
        {
            _auditTrail.Record("Order auto-approved.");
        }

        var total = _pricingStrategy.CalculatePrice(order, tier, peakSeason);
        foreach (var sku in _recommendationService.Recommend(order))
        {
            _auditTrail.Record($"Suggested upsell: {sku}");
        }

        return total;
    }
}
