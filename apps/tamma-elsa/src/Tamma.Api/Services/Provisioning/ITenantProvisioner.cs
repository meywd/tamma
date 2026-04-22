namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Per-tenant provisioning surface. Two implementations:
/// <list type="bullet">
///   <item><description><see cref="NullTenantProvisioner"/> — dev/default
///     when <c>Cranl:ApiKey</c> is absent. Flips the row to
///     <see cref="ProvisioningState.Ready"/> immediately and the tenant
///     rides on the central / shared Postgres via RLS. No external
///     calls.</description></item>
///   <item><description><see cref="CranlTenantProvisioner"/> — production.
///     Walks the README's per-tenant flow (project → db → poll → app
///     → env → deploy → poll → domains).</description></item>
/// </list>
///
/// <para>All methods are idempotent: calling <see cref="ProvisionAsync"/>
/// on a tenant that already has a Cranl project returns the current
/// status without doing anything new.</para>
/// </summary>
public interface ITenantProvisioner
{
    /// <summary>
    /// Trigger provisioning for the tenant. Returns immediately with
    /// <see cref="ProvisioningState.Pending"/> (or the current state if
    /// the tenant already has a project) — the long-running Cranl
    /// polling work runs on a background task.
    /// </summary>
    Task<ProvisioningStatus> ProvisionAsync(
        Guid tenantId, ProvisioningOptions options, CancellationToken ct = default);

    /// <summary>Read the current provisioning state for the tenant.</summary>
    Task<ProvisioningStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Tear down the tenant's Cranl resources. Sequence: delete app, then
    /// db, then project (per Cranl's "project must have no apps before
    /// delete" constraint). Clears all <c>cranl_*</c> columns on success.
    /// </summary>
    Task DeprovisionAsync(Guid tenantId, CancellationToken ct = default);
}
