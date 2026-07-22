using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for <c>acceptance_rules_overrides</c> (Story 39-5). Mirrors
/// <see cref="IPromptRepository"/>'s dual-scoping surface: single-user rows are
/// keyed on <c>UserId</c>, SaaS rows on <c>TenantId</c>; the DB
/// <c>principal_xor</c> CHECK forces exactly-one. <c>documentTypeKey</c> NULL
/// addresses the principal BASE row. The two surfaces are PARALLEL — no method
/// silently joins both planes.
/// </summary>
public interface IAcceptanceRulesRepository
{
    // ───────────────────────── single-user mode ─────────────────────────

    /// <summary>Read a single-user override (tenant_id IS NULL) for the given key (NULL key = base row).</summary>
    Task<AcceptanceRulesOverride?> GetAsync(Guid? userId, string? documentTypeKey);

    /// <summary>
    /// Upsert an override. Returns the persisted entity and a flag: fresh insert
    /// (<c>true</c>) vs update (<c>false</c>) — drives CREATED-vs-UPDATED event
    /// emission. Routes on whichever of <see cref="AcceptanceRulesOverride.UserId"/>
    /// / <see cref="AcceptanceRulesOverride.TenantId"/> is set.
    /// </summary>
    Task<(AcceptanceRulesOverride Entity, bool WasCreated)> UpsertAsync(
        AcceptanceRulesOverride entity, Guid? actingUserId = null);

    Task<bool> DeleteAsync(Guid? userId, string? documentTypeKey);
    Task<List<AcceptanceRulesOverride>> ListAsync(Guid? userId);

    // ───────────────────────── SaaS mode ────────────────────────────────

    /// <summary>Read a tenant-scoped override (user_id IS NULL) for the given key (NULL key = base row).</summary>
    Task<AcceptanceRulesOverride?> GetByTenantAsync(Guid tenantId, string? documentTypeKey);

    /// <summary>Delete a tenant-scoped override. Returns false when no row matched.</summary>
    Task<bool> DeleteByTenantAsync(Guid tenantId, string? documentTypeKey);

    /// <summary>List every tenant-scoped override for <paramref name="tenantId"/>.</summary>
    Task<List<AcceptanceRulesOverride>> ListByTenantAsync(Guid tenantId);
}
