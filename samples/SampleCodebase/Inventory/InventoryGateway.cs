using SampleCodebase.Domain;

namespace SampleCodebase.Inventory;

public sealed class InventoryGateway
{
    public bool HasCapacity(OrderLine line) => line.Quantity <= 20;

    public bool RequiresManualReview(OrderLine line) => line.Quantity > 10;
}
