namespace Tamma.Data.Entities;

public class User
{
    public Guid Id { get; set; }

    /// <summary>
    /// Email address. The C# port keeps this NOT NULL (vs TS migration 002's
    /// nullable column) because JWT claims, dashboard responses, and several
    /// auth code paths assume a non-null email today. OAuth-only users with
    /// no public email synthesize a placeholder via the registration flow.
    /// The DB-level uniqueness is case-insensitive via a partial unique index
    /// on <c>LOWER(email) WHERE deleted_at IS NULL</c> (Phase-1 hardening).
    /// </summary>
    public string Email { get; set; } = null!;

    public string? PasswordHash { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Tenant role. Constrained at the DB level to <c>owner | admin | member</c>
    /// via a CHECK constraint installed by the Phase-1 hardening migration.
    /// </summary>
    public string Role { get; set; } = "member";

    /// <summary>
    /// Platform-wide role (separate from the per-tenant <see cref="Role"/>).
    /// Distinguishes the small set of platform operators (<c>"platform_admin"</c>)
    /// from the regular tenant population (<c>"user"</c>). Story 28-R2/C1
    /// removed the prior "tenant-owner == platform admin" coupling that let
    /// every signed-up user (auto-owner of their personal tenant) pass the
    /// <c>OwnerAccess</c> policy; the new <c>PlatformOwnerAccess</c> policy
    /// keys off this column instead.
    ///
    /// <para>Constrained at the DB level to <c>"user" | "platform_admin"</c>
    /// via the model-level <c>ck_users_platform_role</c> CHECK constraint
    /// (see <c>TammaModelConfiguration</c>). The bootstrap superadmin (the
    /// first user ever created) defaults to <c>platform_admin</c>; every
    /// other registration / invite / OAuth bootstrap defaults to
    /// <c>"user"</c>.</para>
    /// </summary>
    public string PlatformRole { get; set; } = "user";

    public Guid? TenantId { get; set; }
    public bool EmailVerified { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Authentication method. Constrained at the DB level to
    /// <c>email | github | both</c> via a CHECK constraint.
    /// </summary>
    public string AuthMethod { get; set; } = "email";

    /// <summary>
    /// GitHub user ID. Widened to <c>bigint</c> (long) to match TS — GitHub IDs
    /// already exceed 200 million and approach the int32 ceiling of ~2.1B.
    /// </summary>
    public long? GitHubId { get; set; }

    public string? GitHubLogin { get; set; }
    public string? EmailVerificationTokenHash { get; set; }
    public DateTime? EmailVerificationExpiresAt { get; set; }
    public DateTime? LastActiveAt { get; set; }

    /// <summary>
    /// Per-user provider settings (JSON). Restored from TS migration 004 for
    /// per-user provider configuration overrides.
    /// </summary>
    public string Settings { get; set; } = "{}";

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
