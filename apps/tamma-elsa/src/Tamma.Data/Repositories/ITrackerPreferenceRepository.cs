using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for <c>tracker_preferences</c> (Story 44-1 AC6). Mirrors
/// <see cref="IAcceptanceRulesRepository"/>'s dual-scoping surface: single-user
/// rows are keyed on <c>UserId</c> (tenant_id NULL), SaaS rows on
/// <c>TenantId</c> (user_id NULL); the DB's STRONG <c>principal_xor</c> CHECK
/// forces exactly-one. <b>The two surfaces are PARALLEL — no method silently
/// joins both planes</b>: every predicate pins the opposite key to NULL
/// (<c>p.UserId == userId &amp;&amp; p.TenantId == default(Guid?)</c> and the
/// mirror), exactly as <c>AcceptanceRulesRepository</c> does.
/// </summary>
public interface ITrackerPreferenceRepository
{
    // ───────────────────────── single-user mode ─────────────────────────

    /// <summary>Read the user-plane row (tenant_id IS NULL).</summary>
    Task<TrackerPreference?> GetAsync(Guid? userId);

    /// <summary>
    /// Upsert the user-plane row. Returns the persisted entity and a
    /// fresh-insert flag. The entity must carry <c>UserId</c> set and
    /// <c>TenantId</c> null.
    /// </summary>
    Task<(TrackerPreference Entity, bool WasCreated)> UpsertAsync(
        TrackerPreference preference, Guid? actingUserId = null);

    /// <summary>Delete the user-plane row. False when no row matched.</summary>
    Task<bool> DeleteAsync(Guid? userId);

    // ───────────────────────── SaaS mode ────────────────────────────────

    /// <summary>Read the tenant-plane row (user_id IS NULL).</summary>
    Task<TrackerPreference?> GetByTenantAsync(Guid tenantId);

    /// <summary>
    /// Upsert the tenant-plane row. The entity must carry <c>TenantId</c> set
    /// and <c>UserId</c> null.
    /// </summary>
    Task<(TrackerPreference Entity, bool WasCreated)> UpsertForTenantAsync(
        TrackerPreference preference, Guid? actingUserId = null);

    /// <summary>Delete the tenant-plane row. False when no row matched.</summary>
    Task<bool> DeleteByTenantAsync(Guid tenantId);
}
