namespace Tamma.Data.Entities;

public class UserInvite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string? Email { get; set; }
    public string Role { get; set; } = "member";
    public string InviteTokenHash { get; set; } = null!;
    public Guid InvitedBy { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
