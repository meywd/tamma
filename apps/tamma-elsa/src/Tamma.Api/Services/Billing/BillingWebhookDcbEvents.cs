using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 (AC8) — builds the <c>BILLING.*</c> DCB <see cref="DomainEvent"/>
/// rows for webhook projections, in the same shape as <c>BillingEvents</c>:
/// Metadata <c>{"workflowVersion":"1.0.0","eventSource":"system"}</c>; Tags
/// <c>{ tenantId, stripeEventId, eventType, stripeObjectId }</c>. Every projected
/// event carries the resolved <see cref="DomainEvent.TenantId"/> so tenant
/// isolation is structural (AC14) and <c>IEventRepository.AppendAsync</c> routes
/// it to the tenant's stream.
/// </summary>
public static class BillingWebhookDcbEvents
{
    private const string SystemMetadata =
        """{"workflowVersion":"1.0.0","eventSource":"system"}""";

    /// <summary>
    /// A projection event (<c>BILLING.SUBSCRIPTION.*</c>, <c>BILLING.INVOICE.*</c>,
    /// <c>BILLING.PAYMENT.*</c>, <c>BILLING.DISPUTE.OPENED</c>) for a resolved
    /// tenant. <paramref name="data"/> is an optional extra data bag.
    /// </summary>
    public static DomainEvent Projection(
        string dcbType, BillingWebhookContext ctx, object? data = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = dcbType,
            TenantId = ctx.TenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = ctx.TenantId.ToString("D"),
                stripeEventId = ctx.StripeEventId,
                eventType = ctx.EventType,
                stripeObjectId = ctx.StripeObjectId,
            }),
            Metadata = SystemMetadata,
            Data = JsonSerializer.Serialize(data ?? new
            {
                stripeObjectId = ctx.StripeObjectId,
            }),
            CreatedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// An operational event (<c>BILLING.WEBHOOK.SKIPPED</c> /
    /// <c>BILLING.WEBHOOK.FAILED</c>). <paramref name="tenantId"/> is null for a
    /// no-customer-match skip (platform-scoped); the tags carry the raw Stripe id
    /// for forensics without leaking the body.
    /// </summary>
    public static DomainEvent Operational(
        string dcbType, Guid? tenantId, string stripeEventId, string eventType,
        string? stripeObjectId, string reason) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = dcbType,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantId?.ToString("D"),
                stripeEventId,
                eventType,
                stripeObjectId,
            }),
            Metadata = SystemMetadata,
            Data = JsonSerializer.Serialize(new { reason }),
            CreatedAt = DateTime.UtcNow,
        };
}
