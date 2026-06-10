namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Unified-tenancy Phase 2 — the ONE implementation of the tenant
/// provisioning steps, shared by the SaaS CreateTenantWorkflow activities
/// and the single-user EnsurePersonalTenantMiddleware (universal rule:
/// one behavior, two scoping models). Steps are individually idempotent
/// so the Elsa workflow can wrap each in its own activity with retries.
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>Placement (Task 2 seam) — assign pool row + schema name.</summary>
    Task<TenantPlacement> AssignPlacementAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// CREATE ROLE on the placement row's cluster. Returns the generated
    /// password, or null when the role already existed (password
    /// unrecoverable — only the stored envelope from a prior run has it).
    /// </summary>
    Task<string?> CreateRoleAsync(Guid tenantId, TenantPlacement placement, CancellationToken ct = default);

    /// <summary>
    /// CREATE SCHEMA AUTHORIZATION role + GRANT CONNECT + default
    /// search_path, on the placement row's database. Idempotent.
    /// </summary>
    Task CreateSchemaAsync(Guid tenantId, TenantPlacement placement, CancellationToken ct = default);

    /// <summary>Tenant-facing conn string for the placement (Search Path included).</summary>
    Task<string> BuildConnectionStringAsync(
        Guid tenantId, TenantPlacement placement, string password, CancellationToken ct = default);

    /// <summary>
    /// Full pipeline for the synchronous single-user path: placement →
    /// role → schema → conn string → migrate (ITenantDbMigrator) → encrypt
    /// + persist (reusing the activity-equivalent semantics) → Status
    /// 'active'. Throws on any failure — caller decides UX.
    /// </summary>
    Task ProvisionAsync(Guid tenantId, CancellationToken ct = default);
}
