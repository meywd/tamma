namespace Tamma.Data.Abstractions;

/// <summary>
/// Unified-tenancy Phase 3 — the SYSTEM STORE: platform-level default rows
/// (TenantId NULL — conventions, sanitization rules, agent/budget config,
/// provider health) live in the central database's public-schema tenant
/// tables, owned by platform admins. Tenant-scoped rows live in each
/// tenant's t_&lt;hex&gt; schema and are reached via
/// <see cref="ITenantDbContextFactory"/>. This seam replaces the
/// transitional "stub resolver routes Guid.Empty to the shared DB" trick.
/// </summary>
public interface ISystemStoreDbContextFactory
{
    ValueTask<TenantDbContext> CreateAsync(CancellationToken cancellationToken = default);
}
