namespace Tamma.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string? PasswordHash { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = "member";
    public Guid? TenantId { get; set; }
    public bool EmailVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public string AuthMethod { get; set; } = "email";
    public int? GitHubId { get; set; }
    public string? GitHubLogin { get; set; }
    public string? EmailVerificationTokenHash { get; set; }
    public DateTime? EmailVerificationExpiresAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Tenant? Tenant { get; set; }
    public ICollection<TenantMembership> Memberships { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = [];
    public ICollection<ApiKey> ApiKeys { get; set; } = [];
}
