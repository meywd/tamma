using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 31-2 — typed repository over the
/// <c>tenant_platform_installations</c> control-plane table.
///
/// <para>The CP analogue of <see cref="IInstallationRepository"/>:
/// generalises across every <c>PlatformKind</c> the abstraction (Story
/// 31-1) covers, and exposes the lookups the resolver needs
/// (<see cref="GetByTenantPrimaryAsync"/>, <see cref="GetByTenantKindAsync"/>,
/// <see cref="GetByExternalIdAsync"/>) in a single place so cross-cutting
/// concerns (soft-delete filtering, tenant scoping) live behind one
/// seam.</para>
///
/// <para>Soft-delete semantics: every read method excludes rows where
/// <see cref="TenantPlatformInstallation.DeletedAt"/> is non-null. A
/// disconnected installation is restored by
/// <see cref="SoftDeleteAsync"/> + <see cref="RestoreAsync"/>; hard
/// delete is intentionally not exposed (audit retention).</para>
/// </summary>
public interface ITenantPlatformInstallationRepository
{
    /// <summary>
    /// Read the primary installation for a tenant — the row flagged
    /// <see cref="TenantPlatformInstallation.IsPrimary"/>. When a
    /// tenant has only one row, that row is treated as primary
    /// regardless of the flag (idempotent fallback). Returns null
    /// when the tenant has no rows or all rows are soft-deleted.
    /// </summary>
    Task<TenantPlatformInstallation?> GetByTenantPrimaryAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Read the installation for a (tenant, kind) tuple. When the
    /// tenant has multiple rows for that kind, returns the
    /// <see cref="TenantPlatformInstallation.IsPrimary"/> row;
    /// otherwise returns the only matching row. Null when no row
    /// matches.
    /// </summary>
    Task<TenantPlatformInstallation?> GetByTenantKindAsync(
        Guid tenantId,
        string platformKind,
        CancellationToken ct = default);

    /// <summary>
    /// Read by row id. Used by webhook replay tooling and the admin
    /// UI. Soft-deleted rows are not returned.
    /// </summary>
    Task<TenantPlatformInstallation?> GetByIdAsync(
        Guid id, CancellationToken ct = default);

    /// <summary>
    /// Read by platform kind + external id (the value in the webhook
    /// payload). Used by Story 31-7's webhook receiver to find the
    /// owning tenant before signature verification.
    /// </summary>
    Task<TenantPlatformInstallation?> GetByExternalIdAsync(
        string platformKind,
        string installationExternalId,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerate every connected installation for a tenant
    /// (newest-first). Soft-deleted rows are excluded.
    /// </summary>
    Task<IReadOnlyList<TenantPlatformInstallation>> ListByTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Insert a new row. Throws on a unique-index collision (same
    /// tenant + kind + external id, or two primaries per kind).
    /// </summary>
    Task<TenantPlatformInstallation> CreateAsync(
        TenantPlatformInstallation installation,
        CancellationToken ct = default);

    /// <summary>
    /// Update mutable fields on an existing row (status, base URL,
    /// metadata, primary flag). Throws when the row does not exist.
    /// </summary>
    Task<TenantPlatformInstallation> UpdateAsync(
        TenantPlatformInstallation installation,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-delete: stamps <see cref="TenantPlatformInstallation.DeletedAt"/>.
    /// The row stays for audit. Idempotent (already-deleted rows are
    /// a no-op).
    /// </summary>
    Task SoftDeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Restore a soft-deleted row. Idempotent (rows that aren't
    /// soft-deleted are a no-op). Throws when the row doesn't exist
    /// at all.
    /// </summary>
    Task RestoreAsync(Guid id, CancellationToken ct = default);
}
