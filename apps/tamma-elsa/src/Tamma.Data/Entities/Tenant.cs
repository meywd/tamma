namespace Tamma.Data.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Type { get; set; } = "personal";
    public Guid? OwnerId { get; set; }
    public string? ExternalId { get; set; }
    public string Plan { get; set; } = "free";
    public string Settings { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public User? Owner { get; set; }
    public ICollection<TenantMembership> Memberships { get; set; } = [];
    public ICollection<UserInvite> Invites { get; set; } = [];
}
