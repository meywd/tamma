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
    /// <remarks>
    /// Ambient <see cref="ITenantContext.TenantId"/> is required for physical DB
    /// routing even when reading or writing system-default rows
    /// (<c>tenant_id IS NULL</c>): the per-tenant DB factory routes the request
    /// to the correct physical database based on the ambient tenant, regardless
    /// of whether the rows being read/written are system defaults or tenant
    /// overrides. In single-user mode <see cref="EnsurePersonalTenantMiddleware"/>
    /// always binds a personal tenant up-front, so this should never be unset.
    /// </remarks>
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

    /// <remarks>
    /// Ambient <see cref="ITenantContext.TenantId"/> is required for physical DB
    /// routing even though system-default rows carry <c>tenant_id IS NULL</c>
    /// — see <see cref="RequireTenantId"/> doc-comment.
    /// </remarks>
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

    public async Task<(Convention Row, bool WasCreated)> UpsertTenantOverrideAsync(
        Guid tenantId, string role, string action, string body, bool enabled, Guid userId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        // NOTE: check-then-insert — concurrent same-key (tenant_id, role, action)
        // upserts surface as a Postgres unique-violation (23505) via the
        // NULLS NOT DISTINCT unique index (Story 27-8); consistent with
        // PromptRepository on the low-concurrency admin-edit path.

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
            // Story 27-10 — honour the caller's enabled flag on EDIT so a tenant
            // can disable its override (resolution then falls through to system).
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedBy = userId;
            await db.SaveChangesAsync(ct);
            return (existing, false);
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
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };
        db.Conventions.Add(row);
        await db.SaveChangesAsync(ct);
        return (row, true);
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

    /// <remarks>
    /// Ambient <see cref="ITenantContext.TenantId"/> is required for physical DB
    /// routing even though system-default rows carry <c>tenant_id IS NULL</c>
    /// — see <see cref="RequireTenantId"/> doc-comment.
    /// </remarks>
    public async Task<IReadOnlyList<Convention>> ListSystemDefaultsAsync(CancellationToken ct)
    {
        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);
        return await db.Conventions.IgnoreQueryFilters()
            .Where(c => c.TenantId == default(Guid?))
            .ToListAsync(ct);
    }

    /// <remarks>
    /// Ambient <see cref="ITenantContext.TenantId"/> is required for physical DB
    /// routing even though system-default rows carry <c>tenant_id IS NULL</c>
    /// — see <see cref="RequireTenantId"/> doc-comment.
    /// </remarks>
    public async Task<(Convention Row, bool WasCreated)> UpsertSystemDefaultAsync(
        string role, string action, string body, bool enabled, Guid? updatedBy, CancellationToken ct)
    {
        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        // NOTE: check-then-insert — concurrent same-key (NULL, role, action)
        // upserts surface as a Postgres unique-violation (23505) via the
        // NULLS NOT DISTINCT unique index (Story 27-8); consistent with the
        // tenant upsert on the low-concurrency admin-edit path.

        // Match ONLY the system-default row (tenant_id IS NULL) — tenant
        // overrides (tenant_id NOT NULL) are never mutated here (mirror-image
        // of UpsertTenantOverrideAsync's tenant_id = @tenantId discriminator).
        var existing = await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == default(Guid?) && c.Role == role && c.Action == action,
                ct);

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.Body = body;
            existing.Version += 1;
            // Story 27-10 — honour the caller's enabled flag on EDIT so a
            // platform-admin can disable a system default (the reset path passes
            // enabled: true for a canonical restore).
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedBy = updatedBy;
            // Seeded system defaults may have a null creator (the seeder leaves
            // CreatedBy null); stamp the editing admin as the creator the first
            // time one touches the row, never overwriting a real creator.
            existing.CreatedBy ??= updatedBy;
            await db.SaveChangesAsync(ct);
            return (existing, false);
        }

        var row = new Convention
        {
            // Set Id client-side so EF InMemory (test shim) doesn't collide on
            // the Guid.Empty default; production Postgres applies
            // gen_random_uuid() anyway (strict superset — mirrors the seeder).
            Id = Guid.NewGuid(),
            TenantId = null, // system default
            Role = role,
            Action = action,
            Body = body,
            Version = 1,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = updatedBy,
            UpdatedBy = updatedBy,
        };
        db.Conventions.Add(row);
        await db.SaveChangesAsync(ct);
        return (row, true);
    }

    /// <remarks>
    /// Ambient <see cref="ITenantContext.TenantId"/> is required for physical DB
    /// routing even though system-default rows carry <c>tenant_id IS NULL</c>
    /// — see <see cref="RequireTenantId"/> doc-comment.
    /// </remarks>
    public async Task<bool> DeleteSystemDefaultAsync(
        string role, string action, CancellationToken ct)
    {
        var dbTenant = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(dbTenant, ct);

        // tenant_id IS NULL discriminator keeps DELETE off tenant overrides
        // (mirror-image of DeleteTenantOverrideAsync).
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == default(Guid?) && c.Role == role && c.Action == action,
                ct);
        if (row is null) return false;

        db.Conventions.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
