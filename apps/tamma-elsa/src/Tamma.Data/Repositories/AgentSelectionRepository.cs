using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 32-2 — default <see cref="IAgentSelectionRepository"/>. Routes by mode:
/// the single-user (user-keyed) rows live on <see cref="ControlPlaneDbContext"/>;
/// the SaaS (tenant-keyed) rows live in the tenant schema, reached via
/// <see cref="ITenantDbContextFactory"/> + the ambient <see cref="ITenantContext"/>.
/// Same dual-scoping discipline as <see cref="PromptRepository"/>: every read/
/// write carries an explicit principal predicate; no method joins both planes.
/// </summary>
public sealed class AgentSelectionRepository(
    ControlPlaneDbContext cpDb,
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IAgentSelectionRepository
{
    // ───────────────────────── single-user mode ─────────────────────────

    public Task<AgentRoleSelection?> GetByUserAsync(
        Guid userId, string role, CancellationToken ct = default)
        => cpDb.AgentRoleSelections.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.UserId == userId && s.TenantId == default(Guid?) && s.Role == role, ct);

    public async Task<IReadOnlyList<AgentRoleSelection>> ListByUserAsync(
        Guid userId, CancellationToken ct = default)
        => await cpDb.AgentRoleSelections.AsNoTracking()
            .Where(s => s.UserId == userId && s.TenantId == default(Guid?))
            .ToListAsync(ct);

    public Task<(AgentRoleSelection Entity, bool WasCreated)> UpsertByUserAsync(
        Guid userId, string role, Guid agentId, string visibility,
        Guid? updatedBy, CancellationToken ct = default)
        => UpsertAsync(
            cpDb,
            () => cpDb.AgentRoleSelections.FirstOrDefaultAsync(
                s => s.UserId == userId && s.TenantId == default(Guid?) && s.Role == role, ct),
            tenantId: null, userId: userId, role, agentId, visibility, updatedBy, ct);

    // ───────────────────────── SaaS mode ────────────────────────────────

    public async Task<AgentRoleSelection?> GetByTenantAsync(
        Guid tenantId, string role, CancellationToken ct = default)
    {
        await using var db = await tenantDbFactory.CreateAsync(RequireAmbient(), ct);
        return await db.AgentRoleSelections.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.UserId == default(Guid?) && s.Role == role, ct);
    }

    public async Task<IReadOnlyList<AgentRoleSelection>> ListByTenantAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await tenantDbFactory.CreateAsync(RequireAmbient(), ct);
        return await db.AgentRoleSelections.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.UserId == default(Guid?))
            .ToListAsync(ct);
    }

    public async Task<(AgentRoleSelection Entity, bool WasCreated)> UpsertByTenantAsync(
        Guid tenantId, string role, Guid agentId, string visibility,
        Guid? updatedBy, CancellationToken ct = default)
    {
        await using var db = await tenantDbFactory.CreateAsync(RequireAmbient(), ct);
        return await UpsertAsync(
            db,
            () => db.AgentRoleSelections.IgnoreQueryFilters().FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.UserId == default(Guid?) && s.Role == role, ct),
            tenantId: tenantId, userId: null, role, agentId, visibility, updatedBy, ct);
    }

    // ── helpers ──

    private Guid RequireAmbient() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "AgentSelectionRepository (SaaS path) requires an ambient tenant id to "
            + "route the per-tenant connection.");

    /// <summary>
    /// Read-then-write upsert that is resilient to a concurrent first-time
    /// insert race. Two concurrent first-time selects for the same
    /// <c>(principal, role)</c> both read <c>null</c>; one INSERT wins, the loser
    /// hits the unique <c>(TenantId, UserId, Role)</c> violation as a
    /// <see cref="DbUpdateException"/>. We catch the Postgres unique-violation
    /// (scoped precisely to <see cref="Npgsql.PostgresErrorCodes.UniqueViolation"/>,
    /// same as <c>AgentRepository.PublishVersionAsync</c>), detach the failed
    /// insert, re-read the now-present row, and UPDATE it. Selection is
    /// idempotent: last-writer-wins is the correct outcome.
    /// </summary>
    private static async Task<(AgentRoleSelection Entity, bool WasCreated)> UpsertAsync(
        DbContext db,
        Func<Task<AgentRoleSelection?>> readExisting,
        Guid? tenantId, Guid? userId, string role, Guid agentId, string visibility,
        Guid? updatedBy, CancellationToken ct)
    {
        var existing = await readExisting();
        var (entity, wasCreated) = ApplyUpsert(
            db.Set<AgentRoleSelection>(), existing,
            tenantId, userId, role, agentId, visibility, updatedBy);

        try
        {
            await db.SaveChangesAsync(ct);
            return (entity, wasCreated);
        }
        catch (DbUpdateException ex) when (wasCreated && IsUniqueViolation(ex))
        {
            // We lost a first-time-insert race; the winner's row now exists.
            // Detach our orphaned insert, re-read the winner, and update it.
            db.Entry(entity).State = EntityState.Detached;
            var winner = await readExisting()
                ?? throw new InvalidOperationException(
                    "AgentSelectionRepository upsert hit a unique-violation but the "
                    + "conflicting selection row could not be re-read.");
            ApplyUpsert(
                db.Set<AgentRoleSelection>(), winner,
                tenantId, userId, role, agentId, visibility, updatedBy);
            await db.SaveChangesAsync(ct);
            return (winner, false);
        }
    }

    private static (AgentRoleSelection Entity, bool WasCreated) ApplyUpsert(
        DbSet<AgentRoleSelection> set,
        AgentRoleSelection? existing,
        Guid? tenantId, Guid? userId, string role, Guid agentId, string visibility,
        Guid? updatedBy)
    {
        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.AgentId = agentId;
            existing.Visibility = visibility;
            existing.UpdatedAt = now;
            existing.UpdatedBy = updatedBy;
            return (existing, false);
        }
        var row = new AgentRoleSelection
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Role = role,
            AgentId = agentId,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = updatedBy,
        };
        set.Add(row);
        return (row, true);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg &&
           pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
}
