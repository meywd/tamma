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

    public async Task<(AgentRoleSelection Entity, bool WasCreated)> UpsertByUserAsync(
        Guid userId, string role, Guid agentId, string visibility,
        Guid? updatedBy, CancellationToken ct = default)
    {
        var existing = await cpDb.AgentRoleSelections
            .FirstOrDefaultAsync(
                s => s.UserId == userId && s.TenantId == default(Guid?) && s.Role == role, ct);
        var result = ApplyUpsert(
            cpDb.AgentRoleSelections, existing,
            tenantId: null, userId: userId, role, agentId, visibility, updatedBy);
        await cpDb.SaveChangesAsync(ct);
        return result;
    }

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
        var existing = await db.AgentRoleSelections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.UserId == default(Guid?) && s.Role == role, ct);
        var result = ApplyUpsert(
            db.AgentRoleSelections, existing,
            tenantId: tenantId, userId: null, role, agentId, visibility, updatedBy);
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ── helpers ──

    private Guid RequireAmbient() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "AgentSelectionRepository (SaaS path) requires an ambient tenant id to "
            + "route the per-tenant connection.");

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
}
