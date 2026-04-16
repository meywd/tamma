namespace Tamma.Data.Entities;

public class ApiKey
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = null!;
    public string OwnerId { get; set; } = null!;
    public string KeyHash { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public string Label { get; set; } = null!;
    public string[] Permissions { get; set; } = [];
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RotatedFromId { get; set; }
}
