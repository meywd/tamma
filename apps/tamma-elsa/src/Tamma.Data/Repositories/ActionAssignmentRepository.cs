using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <inheritdoc />
/// <remarks>
/// Story 43-5 AC6 — CP context DIRECTLY via
/// <see cref="IDbContextFactory{TContext}"/> (singleton-safe, the
/// <c>EfProviderSettingsRepository</c> seam). Deliberately absent:
/// <c>ITenantDbContextFactory</c>, <c>IgnoreQueryFilters</c>,
/// <c>ApplyTenantFilter</c> — the tenant-residency idiom does not apply to a
/// CP-resident table and would break the platform-ceiling read path (pinned
/// by <c>Reads_DoNotUseTenantDbContextFactory</c>). Every principal query
/// carries the explicit other-key-null predicate (the
/// <c>AcceptanceRulesRepository</c> idiom) so plane isolation is structural.
/// </remarks>
public sealed class EfActionAssignmentRepository : IActionAssignmentRepository
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _factory;

    public EfActionAssignmentRepository(IDbContextFactory<ControlPlaneDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActionAssignment>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ActionAssignments.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActionAssignment>> ListPlatformAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ActionAssignments.AsNoTracking()
            .Where(a => a.TenantId == default(Guid?) && a.UserId == default(Guid?))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActionAssignment>> ListForPrincipalAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default)
    {
        RequireExactlyOnePrincipal(tenantId, userId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Explicit other-key-null predicates: a platform row (both null) can
        // never satisfy either branch — plane isolation is structural.
        return await db.ActionAssignments.AsNoTracking()
            .Where(a => tenantId != null
                ? a.TenantId == tenantId && a.UserId == default(Guid?)
                : a.UserId == userId && a.TenantId == default(Guid?))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(ActionAssignment Entity, bool WasCreated)> UpsertAsync(
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
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException(
                "An action assignment is keyed by AT MOST one principal " +
                "(tenantId XOR userId; both null = the platform ceiling row).");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ActionAssignments
            .FirstOrDefaultAsync(
                a => a.TenantId == tenantId && a.UserId == userId
                    && a.TargetKind == targetKind && a.TargetKey == targetKey,
                ct)
            .ConfigureAwait(false);

        var inserting = row is null;
        if (row is null)
        {
            row = new ActionAssignment
            {
                TenantId = tenantId,
                UserId = userId,
                TargetKind = targetKind,
                TargetKey = targetKey,
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = actingUserId,
            };
            db.ActionAssignments.Add(row);
        }
        else
        {
            row.Version += 1;
        }

        Apply(row, minAutonomy, enforce, enabled, allowedRoles, note, actingUserId);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (row, inserting);
        }
        catch (DbUpdateException ex) when (inserting && IsUniqueViolation(ex))
        {
            // The EfProviderSettingsRepository F8 posture: a concurrent writer
            // inserted the same key between our read and our insert — retry
            // ONCE as an update of the winner's row (last write wins, the same
            // outcome as if the requests had arrived a moment apart).
            db.Entry(row).State = EntityState.Detached;
            var existing = await db.ActionAssignments
                .FirstOrDefaultAsync(
                    a => a.TenantId == tenantId && a.UserId == userId
                        && a.TargetKind == targetKind && a.TargetKey == targetKey,
                    ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            existing.Version += 1;
            Apply(existing, minAutonomy, enforce, enabled, allowedRoles, note, actingUserId);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (existing, false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid? tenantId, Guid? userId, string targetKind, string targetKey,
        CancellationToken ct = default)
    {
        if (tenantId is not null && userId is not null)
        {
            throw new ArgumentException("At most one principal key may be set.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ActionAssignments
            .FirstOrDefaultAsync(
                a => a.TenantId == tenantId && a.UserId == userId
                    && a.TargetKind == targetKind && a.TargetKey == targetKey,
                ct)
            .ConfigureAwait(false);
        if (row is null) return false;

        db.ActionAssignments.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllForPrincipalAsync(
        Guid? tenantId, Guid? userId, CancellationToken ct = default)
    {
        RequireExactlyOnePrincipal(tenantId, userId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ActionAssignments
            .Where(a => tenantId != null
                ? a.TenantId == tenantId && a.UserId == default(Guid?)
                : a.UserId == userId && a.TenantId == default(Guid?))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private static void RequireExactlyOnePrincipal(Guid? tenantId, Guid? userId)
    {
        if ((tenantId is null) == (userId is null))
        {
            throw new ArgumentException(
                "A principal query is keyed by exactly ONE of tenantId / userId. " +
                "The platform plane is read via ListPlatformAsync, never through " +
                "a principal query (Story 43-5 AC6).");
        }
    }

    private static void Apply(
        ActionAssignment row, int? minAutonomy, bool? enforce, bool? enabled,
        string[]? allowedRoles, string? note, Guid? actingUserId)
    {
        // Per-field independence (AC2): a null parameter leaves the stored
        // column alone — a threshold-only write must not silently reset
        // enforce/enabled/roles (the 43-0 acceptorRequirement bug class, one
        // layer down).
        if (minAutonomy is not null) row.MinAutonomy = minAutonomy;
        if (enforce is not null) row.Enforce = enforce;
        if (enabled is not null) row.Enabled = enabled;
        if (allowedRoles is not null) row.AllowedRoles = allowedRoles.Length == 0 ? null : allowedRoles;
        if (note is not null) row.Note = note.Length == 0 ? null : note;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actingUserId;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}
