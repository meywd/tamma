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

    // ── Cranl per-tenant provisioning ──
    //
    // Epic 30 Phase B (Task B3): the six dedicated Cranl columns
    // (CranlProjectId/DatabaseId/AppId/Region/AppUrl + the encrypted
    // CranlDatabaseUrlEncrypted) were dropped. The walk/resume working-state
    // (project/db/app ids, region, engine host) now lives in the
    // `tenants.provider_resource_ids` JSONB (accessed via CranlResourceIds),
    // and the encrypted admin DATABASE_URL lives only on the tenant's
    // `tenant_databases` pool row (AdminConnectionStringEncrypted). The
    // provisioning state machine below is unchanged.

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
