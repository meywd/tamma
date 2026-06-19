using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 (AC8) — builds the DCB <see cref="DomainEvent"/> rows for the two
/// billing events, in the same shape as <c>OrgEndpoints.EmitTenantEvent</c>
/// (Metadata <c>{"workflowVersion":"1.0.0","eventSource":"system"}</c>; Tags +
/// Data JSON-serialized). Event types follow the
/// <c>AGGREGATE.ACTION.STATUS</c> convention.
///
/// <para>Routing is handled by <c>IEventRepository.AppendAsync</c>:
/// <c>BILLING.CUSTOMER.CREATED</c> carries a non-null <c>TenantId</c> so it
/// lands in the tenant's event store; <c>BILLING.PLAN_CATALOG.SYNCED</c> is
/// platform-scoped (<c>TenantId</c> null) so it routes to
/// <c>platform_events</c>.</para>
/// </summary>
public static class BillingEvents
{
    public const string CustomerCreatedType = "BILLING.CUSTOMER.CREATED";
    public const string PlanCatalogSyncedType = "BILLING.PLAN_CATALOG.SYNCED";

    private const string SystemMetadata =
        """{"workflowVersion":"1.0.0","eventSource":"system"}""";

    /// <summary>
    /// <c>BILLING.CUSTOMER.CREATED</c> — emitted only on a real success (Stripe
    /// customer created + row persisted). Tags <c>{ tenantId, stripeCustomerId,
    /// billingMode }</c>; <c>TenantId</c> set.
    /// </summary>
    public static DomainEvent CustomerCreated(
        Guid tenantId, string? stripeCustomerId, string billingMode) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = CustomerCreatedType,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantId.ToString("D"),
                stripeCustomerId,
                billingMode,
            }),
            Metadata = SystemMetadata,
            Data = JsonSerializer.Serialize(new
            {
                stripeCustomerId,
                billingMode,
            }),
            CreatedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// <c>BILLING.PLAN_CATALOG.SYNCED</c> — emitted per slug after a successful
    /// catalog sync. Tags <c>{ planSlug, source: "seed" }</c>; platform-scoped
    /// (<c>TenantId</c> null).
    /// </summary>
    public static DomainEvent PlanCatalogSynced(string planSlug, int created, int reused) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = PlanCatalogSyncedType,
            TenantId = null,
            Tags = JsonSerializer.Serialize(new
            {
                planSlug,
                source = "seed",
            }),
            Metadata = SystemMetadata,
            Data = JsonSerializer.Serialize(new
            {
                planSlug,
                created,
                reused,
            }),
            CreatedAt = DateTime.UtcNow,
        };
}
