namespace SampleCodebase.Orders;

public sealed class AuditTrail
{
    private readonly List<string> _entries = new();

    public void Record(string message) => _entries.Add(message);

    public IReadOnlyList<string> Snapshot() => _entries;
}
