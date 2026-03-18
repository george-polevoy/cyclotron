namespace SampleCodebase.Domain;

public sealed class Order
{
    public Order(string customerId, IReadOnlyList<OrderLine> lines, bool expedited)
    {
        CustomerId = customerId;
        Lines = lines;
        IsExpedited = expedited;
    }

    public string CustomerId { get; }

    public IReadOnlyList<OrderLine> Lines { get; }

    public bool IsExpedited { get; }
}
