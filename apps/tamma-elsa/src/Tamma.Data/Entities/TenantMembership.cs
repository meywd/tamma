namespace Tamma.Data.Entities;

public class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public DateTime JoinedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public User User { get; set; } = null!;
}
