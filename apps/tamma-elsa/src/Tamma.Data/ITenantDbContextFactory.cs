namespace Tamma.Data;

/// <summary>
/// Creates per-tenant <see cref="TenantDbContext"/> instances. Every
/// tenant-scoped repository depends on this factory and calls
/// <see cref="CreateAsync"/> for the tenant it needs, disposing the returned
/// context at the end of the unit of work.
///
/// <para>Epic 28 isolation model: each tenant eventually has its own
/// physical Postgres database; the factory resolves the right connection
/// string per tenant. During the transition (shared central DB) the
/// factory returns a context bound to that tenant via the central
/// connection — but callers are already on the post-transition contract,
/// so flipping to per-tenant routing is a resolver-implementation change
/// (Story 28-4), not a call-site rewrite.</para>
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>
    /// Construct a new <see cref="TenantDbContext"/> bound to the given
    /// tenant. The caller owns disposal — wrap with
    /// <c>await using var ctx = await factory.CreateAsync(tenantId);</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tenantId"/> is <see cref="Guid.Empty"/>.
    /// CP data reads must go through <see cref="ControlPlaneDbContext"/>
    /// directly — never through this factory with an empty tenant id.
    /// </exception>
    Task<TenantDbContext> CreateAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
