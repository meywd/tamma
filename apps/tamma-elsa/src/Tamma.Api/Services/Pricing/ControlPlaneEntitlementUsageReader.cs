using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — control-plane-backed default <see cref="IEntitlementUsageReader"/>
/// answering the three CP-resident gauge counts and returning <c>null</c> for
/// everything metering-backed (Epic 35 supplies that reader):
/// <list type="table">
///   <item><term><see cref="EntitlementMetricKey.Seats"/></term>
///     <description><c>TenantMembership</c> count</description></item>
///   <item><term><see cref="EntitlementMetricKey.Agents"/></term>
///     <description>Epic-32 owned <c>Agent</c> identities — SaaS:
///       <c>OwnerTenantId == tenantId</c>; single-user:
///       <c>OwnerUserId == userId</c> (mode-scoped ownership, see
///       <c>AgentsAsync</c>)</description></item>
///   <item><term><see cref="EntitlementMetricKey.Repos"/></term>
///     <description>active <c>GitHubInstallationRepo</c> for the tenant's installation</description></item>
/// </list>
/// Scoped (depends on the scoped <see cref="ControlPlaneDbContext"/> +
/// <see cref="ITenantMembershipRepository"/>). Read-only.
///
/// <para><b>Spec deviation (documented):</b> Story 34-6 §AC9 named
/// <c>AgentConfig</c> as the <c>Agents</c> source, but that is the deprecated,
/// anonymous, TENANT-resident (unique-per-tenant) config blob — it is not
/// control-plane-resident and cannot be counted here. The canonical Epic-32
/// agent-IDENTITY entity is <c>Agent</c> (control-plane-resident, owned via
/// <c>OwnerTenantId</c>), which is what "number of agent identities a tenant may
/// own" actually means, so the reader counts tenant-owned <c>Agent</c> rows.</para>
/// </summary>
public sealed class ControlPlaneEntitlementUsageReader : IEntitlementUsageReader
{
    private readonly ControlPlaneDbContext _db;
    private readonly ITenantMembershipRepository _memberships;
    private readonly ILogger<ControlPlaneEntitlementUsageReader> _logger;

    public ControlPlaneEntitlementUsageReader(
        ControlPlaneDbContext db,
        ITenantMembershipRepository memberships,
        ILogger<ControlPlaneEntitlementUsageReader> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<long?> GetCurrentAsync(
        Guid tenantId, Guid? userId, EntitlementMetricKey metric, CancellationToken ct = default)
    {
        long? value = metric switch
        {
            EntitlementMetricKey.Seats => await SeatsAsync(tenantId, ct),
            EntitlementMetricKey.Agents => await AgentsAsync(tenantId, userId, ct),
            EntitlementMetricKey.Repos => await ReposAsync(tenantId, ct),

            // Metering-backed — Epic 35 reader answers these.
            _ => null,
        };

        _logger.LogDebug(
            "Usage reader: tenant {TenantId} user {UserId} metric {Metric} → {Value}",
            tenantId, userId, metric.ToMetricString(), value?.ToString() ?? "unavailable");

        return value;
    }

    private async Task<long> SeatsAsync(Guid tenantId, CancellationToken ct)
    {
        // ListByTenantAsync returns (Members, Total); Total is the seat count.
        // limit=1/offset=0 keeps the materialised page tiny — we only want Total.
        var (_, total) = await _memberships.ListByTenantAsync(tenantId, 1, 0);
        return total;
    }

    private Task<int> AgentsAsync(Guid tenantId, Guid? userId, CancellationToken ct) =>
        // Agent identities owned by this principal. Ownership is mode-scoped
        // (Agent entity: OwnerTenantId XOR OwnerUserId), so mirror
        // AgentRepository.ListVisibleAsync's ownership predicate:
        //   • SaaS         → OwnerTenantId == the (resolved) tenant.
        //   • single-user  → OwnerUserId  == the sole user (userId != null);
        //                     the resolved personal tenant owns no agents, so
        //                     the tenant clause contributes 0 and the user
        //                     clause carries the count.
        // Public/system agents (both owner columns NULL) are never counted; the
        // userId guard keeps them out of the SaaS count (userId == null there).
        _db.Agents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                a => a.OwnerTenantId == tenantId
                     || (userId != null && a.OwnerUserId == userId),
                ct);

    private Task<int> ReposAsync(Guid tenantId, CancellationToken ct) =>
        // Active connected repos across the tenant's GitHub installation(s).
        _db.GitHubInstallationRepos
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                r => r.IsActive
                     && _db.GitHubInstallations
                         .Any(i => i.Id == r.InstallationEntityId && i.TenantId == tenantId),
                ct);
}
