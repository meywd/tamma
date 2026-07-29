using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence seam for <c>action_assignments</c> (Story 43-5 AC6). THREE
/// parallel planes — platform (both principal keys null: the ceiling), tenant
/// (SaaS), user (single-user) — and the surfaces are PARALLEL: no method
/// silently joins planes. In particular a principal query must NEVER return a
/// platform-ceiling row — the ceiling is applied by the evaluator via
/// <c>max()</c>, not by union (pinned by
/// <c>Platform_rows_are_never_returned_by_a_principal_query</c>).
///
/// <para>Reads/writes go through the CONTROL-PLANE context directly
/// (<c>IDbContextFactory&lt;ControlPlaneDbContext&gt;</c> — the
/// <c>EfProviderSettingsRepository</c> seam, singleton-safe) — never
/// <c>ITenantDbContextFactory</c>, never <c>IgnoreQueryFilters</c>, never
/// <c>ApplyTenantFilter</c>: those are the tenant-residency idiom, and this
/// table is CP-resident in both modes (see
/// <see cref="Entities.ActionAssignment"/>).</para>
/// </summary>
public interface IActionAssignmentRepository
{
    /// <summary>All rows across all scopes — the snapshot store rebuilds its
    /// whole snapshot from this (bounded: catalog members × principals that
    /// ever saved; tiny by construction).</summary>
    Task<IReadOnlyList<ActionAssignment>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>The PLATFORM ceiling rows only (both principal keys null).</summary>
    Task<IReadOnlyList<ActionAssignment>> ListPlatformAsync(CancellationToken ct = default);

    /// <summary>
    /// One principal's rows: exactly one of <paramref name="tenantId"/> /
    /// <paramref name="userId"/> non-null; the query carries an explicit
    /// other-key-null predicate so a platform row can never leak in. Both
    /// null is rejected — the platform plane is read via
    /// <see cref="ListPlatformAsync"/>, deliberately not through this method.
    /// </summary>
    Task<IReadOnlyList<ActionAssignment>> ListForPrincipalAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default);

    /// <summary>
    /// Upsert one row, keyed by <c>(tenantId, userId, targetKind, targetKey)</c>
    /// — both principal ids null addresses the PLATFORM ceiling row. Null
    /// policy parameters leave the stored column unchanged on update and NULL
    /// on insert (per-field independence, AC2). Returns the persisted row and
    /// whether it was created.
    /// </summary>
    Task<(ActionAssignment Entity, bool WasCreated)> UpsertAsync(
        Guid? tenantId,
        Guid? userId,
        string targetKind,
        string targetKey,
        int? minAutonomy,
        bool? enforce,
        bool? enabled,
        string[]? allowedRoles,
        string? note,
        Guid? actingUserId,
        CancellationToken ct = default);

    /// <summary>Delete one row → resolution falls back to the next tier.
    /// Returns false when no row existed.</summary>
    Task<bool> DeleteAsync(
        Guid? tenantId, Guid? userId, string targetKind, string targetKey,
        CancellationToken ct = default);

    /// <summary>Delete every row for one principal (the policy reset).
    /// Rejects the both-null platform principal — a platform reset must be an
    /// explicit per-row act. Returns the number of rows removed.</summary>
    Task<int> DeleteAllForPrincipalAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default);
}
