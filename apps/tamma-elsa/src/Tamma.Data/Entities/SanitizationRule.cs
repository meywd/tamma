namespace Tamma.Data.Entities;

public class SanitizationRule
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Rules { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
