namespace Tamma.Data.Entities;

public class DomainEvent
{
    public Guid Id { get; set; }
    public string Type { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public int? IssueNumber { get; set; }
    public string Tags { get; set; } = "{}";
    public string Metadata { get; set; } = "{}";
    public string Data { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
