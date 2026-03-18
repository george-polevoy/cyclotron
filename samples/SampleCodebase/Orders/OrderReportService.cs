using SampleCodebase.Domain;
using SampleCodebase.Recommendations;

namespace SampleCodebase.Orders;

public sealed class OrderReportService
{
    private readonly AuditTrail _auditTrail;
    private readonly RecommendationService _recommendationService;

    public OrderReportService(AuditTrail auditTrail, RecommendationService recommendationService)
    {
        _auditTrail = auditTrail;
        _recommendationService = recommendationService;
    }

    public IReadOnlyList<string> BuildReport(Order order)
    {
        var report = new List<string>(_auditTrail.Snapshot());
        report.AddRange(_recommendationService.Recommend(order));
        report.Add($"Customer: {order.CustomerId}");
        return report;
    }
}
