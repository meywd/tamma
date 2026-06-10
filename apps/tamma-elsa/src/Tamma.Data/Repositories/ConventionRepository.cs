using Microsoft.EntityFrameworkCore;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped convention store persistence (Story 27-9).
///
/// <para><b>Two stores, one repository (unified-tenancy Phase 3).</b>
/// Tenant-override rows (<c>tenant_id = X</c>) live in the calling tenant's
/// physical store and route through <see cref="ITenantDbContextFactory"/> with
/// the ambient <see cref="ITenantContext"/> tenant id. System-default rows
/// (<c>tenant_id IS NULL</c>, seeded by Story 27-16 / managed by Story 27-10
/// admin CRUD) live in the SYSTEM STORE — the central DB's public-schema
/// <c>conventions</c> table — and route through
/// <see cref="ISystemStoreDbContextFactory"/>, with no ambient tenant
/// required. The <c>tenant_id</c> predicates still discriminate the row tier
/// inside each store.</para>
/// </summary>
public class ConventionRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext,
    ISystemStoreDbContextFactory systemStoreFactory) : IConventionRepository
{
    /// <remarks>
    /// Ambient <see cref="ITenantContext.TenantId"/> is required for physical DB
    /// routing of TENANT-OVERRIDE rows: the per-tenant DB factory routes the
    /// request to the calling tenant's physical database. System-default rows
    /// do NOT use this — they route through the system store. In single-user
    /// mode <see cref="EnsurePersonalTenantMiddleware"/> always binds a personal
    /// tenant up-front, so this should never be unset.
    /// </remarks>
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "ConventionRepository requires an ambient tenant id. The per-tenant "
            + "DB factory routes tenant-override rows through the calling "
            + "tenant's physical database — single-user mode binds a personal "
            + "tenant up-front (EnsurePersonalTenantMiddleware), so this should "
            + "always be set.");

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
    /// System-default rows (<c>tenant_id IS NULL</c>) live in the SYSTEM STORE
    /// (central DB public schema) — no ambient tenant id required.
    /// </remarks>
    public async Task<Convention?> GetSystemDefaultAsync(
        string role, string action, CancellationToken ct)
    {
        await using var db = await systemStoreFactory.CreateAsync(ct);
        return await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == default(Guid?) && c.Role == role && c.Action == action,
                ct);
    }

    public async Task<(Convention Row, bool WasCreated, Convention? PreviousRow)> UpsertTenantOverrideAsync(
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
            // Snapshot the previous state BEFORE mutating (for changedFields diff).
            var previous = new Convention
            {
                Id = existing.Id,
                TenantId = existing.TenantId,
                Role = existing.Role,
                Action = existing.Action,
                Body = existing.Body,
                Version = existing.Version,
                Enabled = existing.Enabled,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt,
                CreatedBy = existing.CreatedBy,
                UpdatedBy = existing.UpdatedBy,
            };
            existing.Body = body;
            existing.Version += 1;
            // Story 27-10 — honour the caller's enabled flag on EDIT so a tenant
            // can disable its override (resolution then falls through to system).
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedBy = userId;
            await db.SaveChangesAsync(ct);
            return (existing, false, previous);
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
        return (row, true, null);
    }

    public async Task<(bool WasDeleted, int? DeletedVersion)> DeleteTenantOverrideAsync(
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
        if (row is null) return (false, null);

        var deletedVersion = row.Version;
        db.Conventions.Remove(row);
        await db.SaveChangesAsync(ct);
        return (true, deletedVersion);
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
    /// System-default rows (<c>tenant_id IS NULL</c>) live in the SYSTEM STORE
    /// (central DB public schema) — no ambient tenant id required.
    /// </remarks>
    public async Task<IReadOnlyList<Convention>> ListSystemDefaultsAsync(CancellationToken ct)
    {
        await using var db = await systemStoreFactory.CreateAsync(ct);
        return await db.Conventions.IgnoreQueryFilters()
            .Where(c => c.TenantId == default(Guid?))
            .ToListAsync(ct);
    }

    /// <remarks>
    /// System-default rows (<c>tenant_id IS NULL</c>) live in the SYSTEM STORE
    /// (central DB public schema) — no ambient tenant id required.
    /// </remarks>
    public async Task<(Convention Row, bool WasCreated, Convention? PreviousRow)> UpsertSystemDefaultAsync(
        string role, string action, string body, bool enabled, Guid? updatedBy, CancellationToken ct)
    {
        await using var db = await systemStoreFactory.CreateAsync(ct);

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
            // Snapshot the previous state BEFORE mutating (for changedFields diff).
            var previous = new Convention
            {
                Id = existing.Id,
                TenantId = existing.TenantId,
                Role = existing.Role,
                Action = existing.Action,
                Body = existing.Body,
                Version = existing.Version,
                Enabled = existing.Enabled,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = existing.UpdatedAt,
                CreatedBy = existing.CreatedBy,
                UpdatedBy = existing.UpdatedBy,
            };
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
            return (existing, false, previous);
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
        return (row, true, null);
    }

    /// <remarks>
    /// System-default rows (<c>tenant_id IS NULL</c>) live in the SYSTEM STORE
    /// (central DB public schema) — no ambient tenant id required.
    /// </remarks>
    public async Task<(bool WasDeleted, int? DeletedVersion)> DeleteSystemDefaultAsync(
        string role, string action, CancellationToken ct)
    {
        await using var db = await systemStoreFactory.CreateAsync(ct);

        // tenant_id IS NULL discriminator keeps DELETE off tenant overrides
        // (mirror-image of DeleteTenantOverrideAsync).
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == default(Guid?) && c.Role == role && c.Action == action,
                ct);
        if (row is null) return (false, null);

        var deletedVersion = row.Version;
        db.Conventions.Remove(row);
        await db.SaveChangesAsync(ct);
        return (true, deletedVersion);
    }
}
