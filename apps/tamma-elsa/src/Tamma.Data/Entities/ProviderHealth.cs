namespace Tamma.Data.Entities;

public class ProviderHealth
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;
    public string Status { get; set; } = "unknown";
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public int FailureCount { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
