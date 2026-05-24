using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped convention store persistence (Story 27-9). Uses
/// <see cref="ITenantDbContextFactory"/> for all reads/writes; the ambient
/// <see cref="ITenantContext"/> MUST carry a tenant id (mirrors
/// <see cref="PromptRepository"/> — Story 28-1 PR D).
///
/// <para><b>DB routing vs row scoping.</b> The ambient tenant id
/// (<see cref="RequireTenantId"/>) picks the PHYSICAL database via the factory.
/// The <c>tenantId</c> / <c>tenant_id IS NULL</c> predicates select WHICH ROWS
/// (tenant-override tier vs system-default tier) within that database — the
/// system defaults seeded by Story 27-16 live in the same per-tenant DB as the
/// overrides. In the transitional shared-DB model the factory routes every
/// tenant to one physical DB anyway, so a single index seek on
/// <c>(tenant_id, role, action)</c> (the <c>NULLS NOT DISTINCT</c> unique index
/// from Story 27-8) resolves both tiers.</para>
/// </summary>
public class ConventionRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IConventionRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "ConventionRepository requires an ambient tenant id. The per-tenant "
            + "DB factory routes both system-default (tenant_id IS NULL) and "
            + "tenant-override rows through the calling tenant's physical "
            + "database — single-user mode binds a personal tenant up-front "
            + "(EnsurePersonalTenantMiddleware), so this should always be set.");

    public async Task<Convention?> GetTenantOverrideAsync(
        Guid tenantId, string role, string action, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        return await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId && c.Role == role && c.Action == action,
                ct);
    }

    public async Task<Convention?> GetSystemDefaultAsync(
        string role, string action, CancellationToken ct)
    {
        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        return await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == default(Guid?) && c.Role == role && c.Action == action,
                ct);
    }

    public async Task<Convention> UpsertTenantOverrideAsync(
        Guid tenantId, string role, string action, string body, Guid userId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        // Match ONLY tenant-override rows — system defaults (tenant_id IS NULL)
        // are never mutated here (AC2).
        var existing = await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId && c.Role == role && c.Action == action,
                ct);

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.Body = body;
            existing.Version += 1;
            existing.Enabled = true;
            existing.UpdatedAt = now;
            existing.UpdatedBy = userId;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var row = new Convention
        {
            // Set Id client-side so EF InMemory (test shim) doesn't collide on
            // the Guid.Empty default; production Postgres applies
            // gen_random_uuid() anyway (strict superset — mirrors the seeder).
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Role = role,
            Action = action,
            Body = body,
            Version = 1,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };
        db.Conventions.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteTenantOverrideAsync(
        Guid tenantId, string role, string action, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        // tenant_id = @tenantId discriminator keeps DELETE off system defaults (AC2).
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId && c.Role == role && c.Action == action,
                ct);
        if (row is null) return false;

        db.Conventions.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Convention>> ListTenantOverridesAsync(
        Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        return await db.Conventions.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Convention>> ListSystemDefaultsAsync(CancellationToken ct)
    {
        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        return await db.Conventions.IgnoreQueryFilters()
            .Where(c => c.TenantId == default(Guid?))
            .ToListAsync(ct);
    }
}
