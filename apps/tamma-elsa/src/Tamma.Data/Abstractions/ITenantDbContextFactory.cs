namespace Tamma.Data.Abstractions;

/// <summary>
/// Builds a fresh <see cref="TenantDbContext"/> bound to the resolved
/// per-tenant connection. Each call returns a NEW instance so callers
/// can use <c>await using</c> for proper async disposal — tracking
/// state and the change tracker are scoped to the request, not the
/// process.
///
/// <para>Story 28-3 ships this contract + the default implementation
/// that delegates to <see cref="ITenantConnectionResolver"/>; Story
/// 28-4 lands the real per-tenant pool behind the resolver.</para>
///
/// <para>Usage pattern (endpoint layer, Story 28-9 onward):</para>
/// <code>
/// await using var ctx = await factory.CreateAsync(tenantContext.TenantId, ct);
/// var rows = await ctx.AgentConfigs.ToListAsync(ct);
/// </code>
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>
    /// Builds a <see cref="TenantDbContext"/> bound to the data source
    /// for <paramref name="tenantId"/>. The returned context owns its
    /// connection scope; callers must dispose it (preferably via
    /// <c>await using</c>).
    /// </summary>
    ValueTask<TenantDbContext> CreateAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
