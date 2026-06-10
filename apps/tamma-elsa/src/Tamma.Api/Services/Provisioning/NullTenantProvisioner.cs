using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Default <see cref="ITenantProvisioner"/> when <c>Cranl:ApiKey</c> is
/// not configured. Mints no external resources — the row is flipped to
/// <see cref="ProvisioningState.Ready"/> immediately and the tenant's
/// placement stays on the unified tenant_databases pool (central DB by
/// default). No external Cranl calls are made.
///
/// <para>This is the seam that keeps dev / self-hosted deployments
/// working without a Cranl account.</para>
///
/// <para><b>Story 30-3 — DEPRECATED.</b> The v2 equivalent is
/// <see cref="V2.NullTenantProvider"/>, which throws
/// <see cref="NotSupportedException"/> on provisioning calls instead of
/// faking a "Ready" state. Single-user mode still relies on this v1 seam
/// while admin endpoints route through the v1 surface; Wave-C migrates
/// admin endpoints to the v2 registry and retires this class.</para>
/// </summary>
[Obsolete("Use ITenantInfrastructureProvider (V2) instead. Removed in Wave C.")]
public sealed class NullTenantProvisioner : ITenantProvisioner
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<NullTenantProvisioner> _logger;

    public NullTenantProvisioner(ControlPlaneDbContext db, ILogger<NullTenantProvisioner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ProvisioningStatus> ProvisionAsync(
        Guid tenantId, ProvisioningOptions options, CancellationToken ct = default)
    {
        var tenant = await GetTenantAsync(tenantId, ct);
        var now = DateTime.UtcNow;
        tenant.ProvisioningState = ProvisioningState.Ready.ToStorageString();
        tenant.ProvisioningDetail =
            "shared_infrastructure_no_cranl_configured";
        tenant.ProvisioningUpdatedAt = now;
        tenant.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "NullTenantProvisioner: tenant {TenantId} marked Ready (shared infra fallback)",
            tenantId);

        return ToStatus(tenant);
    }

    public async Task<ProvisioningStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await GetTenantAsync(tenantId, ct);
        return ToStatus(tenant);
    }

    public async Task DeprovisionAsync(Guid tenantId, CancellationToken ct = default)
    {
        // No external resources to tear down — flip the state for symmetry
        // so the admin endpoint behaves consistently.
        var tenant = await GetTenantAsync(tenantId, ct);
        var now = DateTime.UtcNow;
        tenant.ProvisioningState = ProvisioningState.Deprovisioned.ToStorageString();
        tenant.ProvisioningDetail = "shared_infrastructure_deprovision_noop";
        tenant.ProvisioningUpdatedAt = now;
        tenant.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Tenant> GetTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        return tenant;
    }

    private static ProvisioningStatus ToStatus(Tenant tenant) =>
        new(
            ProvisioningStateExtensions.ParseState(tenant.ProvisioningState),
            tenant.ProvisioningDetail,
            tenant.CranlAppUrl,
            tenant.ProvisioningUpdatedAt is { } u
                ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow);
}
