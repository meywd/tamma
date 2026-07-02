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

    // ── Story 35-4 — subscription lifecycle DCB event types (AC8). Emitted by
    //    SubscriptionMirrorUpdater on every transition; tags { tenantId, planSlug,
    //    status }. Names follow the AGGREGATE.ACTION.STATUS convention. ──
    public const string SubscriptionCreatedType = "BILLING.SUBSCRIPTION.CREATED";
    public const string SubscriptionUpdatedType = "BILLING.SUBSCRIPTION.UPDATED";
    public const string SubscriptionCanceledType = "BILLING.SUBSCRIPTION.CANCELED";
    public const string SubscriptionTrialEndedType = "BILLING.SUBSCRIPTION.TRIAL_ENDED";

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
    /// Story 35-4 (AC8) — a subscription-lifecycle DCB event. <paramref name="type"/>
    /// is one of the <c>Subscription*Type</c> constants; tags are
    /// <c>{ tenantId, planSlug, status }</c> (plus optional
    /// <paramref name="scheduledPlanSlug"/> on a scheduled downgrade). Tenant-scoped
    /// (<c>TenantId</c> set) so <c>IEventRepository.AppendAsync</c> routes it to the
    /// tenant's own <c>DomainEvents</c> store.
    /// </summary>
    public static DomainEvent Subscription(
        string type, Guid tenantId, string planSlug, string status,
        string? scheduledPlanSlug = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantId.ToString("D"),
                planSlug,
                status,
            }),
            Metadata = SystemMetadata,
            Data = JsonSerializer.Serialize(new
            {
                planSlug,
                status,
                scheduledPlanSlug,
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
