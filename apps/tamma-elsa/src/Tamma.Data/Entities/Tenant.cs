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

    // ── Cranl per-tenant provisioning (audit cranl/001 — Doc 02 §3) ──
    //
    // Populated when a platform owner provisions the tenant onto Cranl
    // (via POST /api/admin/tenants/{id}/provision). NULL when Cranl has
    // not minted hosting infrastructure for the tenant — the default;
    // placement is owned by the unified schema-per-tenant model
    // (SchemaName + DatabaseId against the tenant_databases pool).
    public string? CranlProjectId { get; set; }
    public string? CranlDatabaseId { get; set; }
    public string? CranlAppId { get; set; }
    public string? CranlRegion { get; set; }

    /// <summary>
    /// Encrypted DATABASE_URL handed back by Cranl after the database
    /// reaches <c>running</c>. Encrypted at rest with AES-GCM keyed
    /// from <c>Cranl:EncryptionKey</c> (or the HKDF fallback noted
    /// in TenantSecretProtector). NULL when Cranl never minted a
    /// database for the tenant — routing then uses the tenant's
    /// unified-model connection string (EncryptedConnectionString).
    /// </summary>
    public byte[]? CranlDatabaseUrlEncrypted { get; set; }

    /// <summary>
    /// Default <c>*.cranl.net</c> hostname for the tenant's Elsa app
    /// (e.g. <c>tamma-engine-abc123.cranl.net</c>). Populated by the
    /// final step of the provisioning flow.
    /// </summary>
    public string? CranlAppUrl { get; set; }

    /// <summary>
    /// Provisioning state machine — string-encoded
    /// <see cref="Tamma.Api.Services.Provisioning.ProvisioningState"/> ('none' →
    /// 'pending' → 'database_provisioning' → ... → 'ready').
    /// </summary>
    public string ProvisioningState { get; set; } = "none";

    /// <summary>Free-text status detail for the most recent transition.</summary>
    public string? ProvisioningDetail { get; set; }

    /// <summary>Timestamp of the most recent provisioning state change.</summary>
    public DateTime? ProvisioningUpdatedAt { get; set; }

    public User? Owner { get; set; }
    public ICollection<TenantMembership> Memberships { get; set; } = [];
    public ICollection<UserInvite> Invites { get; set; } = [];
}
