using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for the <c>conventions</c> table (Story 27-9).
///
/// <para>Unlike <see cref="IPromptRepository"/>, conventions are a <b>two-tier,
/// tenant-scoped</b> model: <c>tenant_id IS NULL</c> identifies a system
/// default (seeded by Story 27-16), <c>tenant_id = X</c> identifies tenant X's
/// override. There is NO <c>user_id</c> column and NO per-user override layer
/// (see <see cref="Convention"/>). The system defaults live in the DB (one row
/// per taxonomy cell) — NOT in code — so this repository reads BOTH tiers from
/// the same per-tenant physical database.</para>
///
/// <para>All reads/writes route through <see cref="Abstractions.ITenantDbContextFactory"/>
/// using the ambient request tenant id (<see cref="ITenantContext.TenantId"/>)
/// to pick the physical DB — exactly like <see cref="PromptRepository"/>. The
/// <c>tenantId</c> argument here selects WHICH ROWS to read/write within that
/// DB (system-default vs tenant-override tier), not which database to target.</para>
/// </summary>
public interface IConventionRepository
{
    /// <summary>
    /// Read a tenant-override row (<c>tenant_id = @tenantId</c>) for the given
    /// <c>(role, action)</c> wire strings. Returns null when no row matches.
    /// Tenant overrides are stored/read regardless of <see cref="Convention.Enabled"/>;
    /// the service layer applies the enabled filter during resolution.
    /// </summary>
    Task<Convention?> GetTenantOverrideAsync(
        Guid tenantId, string role, string action, CancellationToken ct);

    /// <summary>
    /// Read the system-default row (<c>tenant_id IS NULL</c>) for the given
    /// <c>(role, action)</c> wire strings. Returns null when no row matches.
    /// </summary>
    Task<Convention?> GetSystemDefaultAsync(
        string role, string action, CancellationToken ct);

    /// <summary>
    /// Upsert a tenant-override row. Operates ONLY on the tenant tier
    /// (<c>tenant_id = @tenantId</c>) — system defaults (<c>tenant_id IS NULL</c>)
    /// are never touched. Sets <see cref="Convention.CreatedBy"/> /
    /// <see cref="Convention.UpdatedBy"/> to <paramref name="userId"/>, sets
    /// <see cref="Convention.Enabled"/> to <paramref name="enabled"/> on both
    /// insert and update (Story 27-10 — a tenant edit must be able to disable an
    /// override without silently re-enabling it), and bumps
    /// <see cref="Convention.Version"/> on update. Returns the persisted entity,
    /// a <c>wasCreated</c> flag (<c>true</c> = INSERT, <c>false</c> = UPDATE),
    /// and the <c>previousRow</c> snapshot (non-null on UPDATE, null on INSERT)
    /// so callers can compute a <c>changedFields</c> diff for the DCB UPDATED event.
    /// </summary>
    Task<(Convention Row, bool WasCreated, Convention? PreviousRow)> UpsertTenantOverrideAsync(
        Guid tenantId, string role, string action, string body, bool enabled, Guid userId, CancellationToken ct);

    /// <summary>
    /// Delete a tenant-override row. Operates ONLY on the tenant tier — system
    /// defaults are never deleted. Returns false when no tenant override matched
    /// (caller falls back to the system default); when a row IS deleted, the
    /// <c>deletedVersion</c> field carries its version at deletion time (for the
    /// DCB DELETED event's <c>data.deletedVersion</c>).
    /// </summary>
    Task<(bool WasDeleted, int? DeletedVersion)> DeleteTenantOverrideAsync(
        Guid tenantId, string role, string action, CancellationToken ct);

    /// <summary>
    /// List every tenant-override row for <paramref name="tenantId"/>
    /// (<c>tenant_id = @tenantId</c>), keyed by <c>(role, action)</c>. Used by
    /// the service to overlay overrides on top of the system-default set when
    /// building the full resolved list.
    /// </summary>
    Task<IReadOnlyList<Convention>> ListTenantOverridesAsync(
        Guid tenantId, CancellationToken ct);

    /// <summary>
    /// List every system-default row (<c>tenant_id IS NULL</c>). Used by the
    /// service to build the full resolved list across all taxonomy cells.
    /// </summary>
    Task<IReadOnlyList<Convention>> ListSystemDefaultsAsync(CancellationToken ct);

    // ------------------------------------------------------------------
    // System-default admin CRUD (Story 27-10 enablement). These operate on the
    // SYSTEM-DEFAULT tier (tenant_id IS NULL) and are deliberately named
    // distinct from the tenant-override methods (NOT overloads) so a caller can
    // never confuse the two tiers — mirrors the prompt-store
    // overload-resolution-hazard convention. They are the MUTATION-SAFE
    // mirror-image of the tenant methods: they NEVER touch tenant-override rows
    // (tenant_id NOT NULL), just as the tenant methods never touch
    // system defaults.
    // ------------------------------------------------------------------

    /// <summary>
    /// Insert-or-update the SYSTEM-DEFAULT row (<c>tenant_id IS NULL</c>) for
    /// <c>(role, action)</c>. Operates ONLY on the system-default tier — tenant
    /// overrides (<c>tenant_id NOT NULL</c>) are never touched. Sets
    /// <see cref="Convention.Enabled"/> to <paramref name="enabled"/> on both
    /// insert and update (Story 27-10 — a platform-admin edit must be able to
    /// disable a system default without silently re-enabling it; the reset path
    /// passes <c>enabled: true</c> for a canonical restore). Bumps
    /// <see cref="Convention.Version"/> on update and sets
    /// <see cref="Convention.UpdatedBy"/> (and <see cref="Convention.CreatedBy"/>
    /// when inserting, or when an existing seeded row still has a null creator)
    /// to <paramref name="updatedBy"/> when provided. Returns the persisted entity,
    /// a <c>wasCreated</c> flag (<c>true</c> = INSERT, <c>false</c> = UPDATE),
    /// and the <c>previousRow</c> snapshot (non-null on UPDATE, null on INSERT)
    /// so callers can compute a <c>changedFields</c> diff for the DCB UPDATED
    /// event. Same check-then-insert pattern as the tenant upsert — a concurrent
    /// same-key insert surfaces as a Postgres unique-violation (23505) via the
    /// <c>NULLS NOT DISTINCT</c> unique index.
    /// </summary>
    Task<(Convention Row, bool WasCreated, Convention? PreviousRow)> UpsertSystemDefaultAsync(
        string role, string action, string body, bool enabled, Guid? updatedBy, CancellationToken ct);

    /// <summary>
    /// Delete the SYSTEM-DEFAULT row (<c>tenant_id IS NULL</c>) for
    /// <c>(role, action)</c>. Operates ONLY on the system-default tier — tenant
    /// overrides are never deleted. Returns false when no system default matched;
    /// when a row IS deleted, the <c>deletedVersion</c> field carries its version
    /// at deletion time (for the DCB DELETED event's <c>data.deletedVersion</c>).
    /// </summary>
    Task<(bool WasDeleted, int? DeletedVersion)> DeleteSystemDefaultAsync(
        string role, string action, CancellationToken ct);
}
