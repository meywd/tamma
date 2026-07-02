using System.Security.Cryptography;
using System.Text;
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
///
/// <para><b>Exactly-once on replay.</b> The <see cref="DomainEvent.Id"/> is
/// DETERMINISTIC — derived from <c>(dcbType, stripeEventId)</c> via
/// <see cref="DeterministicId"/> — so re-dispatching the same fact (admin replay
/// of a non-terminal row, or a crash between the DCB emit and the terminal status
/// save) mints the SAME Id. <c>IEventRepository.AppendAsync</c> (tenant stream)
/// and <c>IPlatformEventRepository.AppendAsync</c> (platform stream, PK collision)
/// both dedup on that Id, so a re-dispatch is idempotent and money events
/// (<c>BILLING.INVOICE.PAID</c>, <c>BILLING.PAYMENT.SUCCEEDED</c>) are never
/// double-counted. A random <c>Guid.NewGuid()</c> here would defeat that dedup.</para>
/// </summary>
public static class BillingWebhookDcbEvents
{
    private const string SystemMetadata =
        """{"workflowVersion":"1.0.0","eventSource":"system"}""";

    /// <summary>
    /// Fixed namespace for the name-based (UUIDv5-style) deterministic Id. Not
    /// security-sensitive — it only namespaces the SHA-1 name hash so unrelated
    /// deterministic-Guid schemes cannot collide.
    /// </summary>
    private static readonly Guid IdNamespace = new("a3f1c2d4-5b6e-4788-9a0b-1c2d3e4f5a6b");

    /// <summary>
    /// A projection event (<c>BILLING.SUBSCRIPTION.*</c>, <c>BILLING.INVOICE.*</c>,
    /// <c>BILLING.PAYMENT.*</c>, <c>BILLING.DISPUTE.OPENED</c>) for a resolved
    /// tenant. <paramref name="data"/> is an optional extra data bag.
    /// </summary>
    public static DomainEvent Projection(
        string dcbType, BillingWebhookContext ctx, object? data = null) =>
        new()
        {
            Id = DeterministicId(dcbType, ctx.StripeEventId),
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
            Id = DeterministicId(dcbType, stripeEventId),
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

    /// <summary>
    /// Deterministic, name-based (RFC 4122 §4.3 UUIDv5) Guid over
    /// <c>(dcbType, stripeEventId)</c>. Same inputs → same Guid, so a re-dispatch
    /// of the same DCB fact is deduped by the event store on its Id. Different
    /// dcbTypes for the same Stripe event (e.g. a projection vs a
    /// <c>BILLING.WEBHOOK.SKIPPED</c>) are DISTINCT facts and get distinct Ids.
    /// </summary>
    internal static Guid DeterministicId(string dcbType, string stripeEventId)
    {
        // Length-prefix dcbType so ("A","BC") and ("AB","C") can never hash to the
        // same name (unambiguous, no separator/control chars).
        var name = Encoding.UTF8.GetBytes(
            $"{(dcbType ?? string.Empty).Length}:{dcbType}{stripeEventId}");
        var ns = IdNamespace.ToByteArray();
        SwapGuidByteOrder(ns); // Guid stores the first 3 fields little-endian; UUIDv5 hashes network order.

        var buffer = new byte[ns.Length + name.Length];
        Buffer.BlockCopy(ns, 0, buffer, 0, ns.Length);
        Buffer.BlockCopy(name, 0, buffer, ns.Length, name.Length);

        var hash = SHA1.HashData(buffer); // 20 bytes; take the first 16.
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapGuidByteOrder(guidBytes); // back to Guid's mixed-endian layout
        return new Guid(guidBytes);
    }

    /// <summary>Swap the byte order of a Guid's first three fields (endianness bridge).</summary>
    private static void SwapGuidByteOrder(byte[] g)
    {
        (g[0], g[3]) = (g[3], g[0]);
        (g[1], g[2]) = (g[2], g[1]);
        (g[4], g[5]) = (g[5], g[4]);
        (g[6], g[7]) = (g[7], g[6]);
    }
}
