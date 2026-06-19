namespace Tamma.Data.Entities;

/// <summary>
/// Story 35-1 — the tenant → Stripe customer mapping. Exactly one row per
/// tenant (unique <see cref="TenantId"/>), SaaS only. The mapping is written by
/// <c>BillingTenantCreateHook</c>, which runs AFTER the tenant-create commit and
/// the <c>TENANT.CREATED.SUCCESS</c> event — NOT inside the tenant-create
/// transaction — so a billing failure can never roll the tenant back. On the
/// happy path the row is created with a non-null <see cref="StripeCustomerId"/>.
/// When Stripe is unreachable at create time no row is written here; a
/// <c>billing.customer.create</c> retry task is enqueued and the retry handler
/// creates (or fills in) the row on a later attempt — tenant creation is never
/// blocked on Stripe.
///
/// <para>The <see cref="StripeCustomerId"/> can still be null for a row created
/// by a retry attempt that itself reached Stripe but had not yet acked the
/// customer id; the next retry fills it in.</para>
///
/// <para>CP-resident (control plane): the customer binding is a cross-cutting
/// billing concern keyed by tenant, not tenant-resident business data. In
/// single-user mode no row is ever written (<c>NullBillingProvider</c>).</para>
/// </summary>
public class BillingCustomer
{
    /// <summary>Stable id (server default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant — unique FK to <c>tenants.Id</c>.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stripe customer id (<c>cus_...</c>). Non-null on the happy path (the row
    /// is only written after Stripe acks the customer). Nullable so a row created
    /// by a partially-failed retry can persist without an id; the retry handler
    /// fills it in on a later attempt.
    /// </summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// Billing mode persisted as the <see cref="Core.Billing.BillingMode"/>
    /// member name (<c>PlatformProvided</c> | <c>Byok</c>). Text domain pinned
    /// by a CHECK constraint. Defaults to <c>PlatformProvided</c>.
    /// </summary>
    public string BillingMode { get; set; } = "PlatformProvided";

    /// <summary>ISO-4217 default settlement currency. Defaults to <c>usd</c>.</summary>
    public string DefaultCurrency { get; set; } = "usd";

    /// <summary>
    /// Tax handling marker (<c>none</c> | <c>taxable</c> | <c>reverse_charge</c>).
    /// Stored verbatim; tax computation is a later Epic 35 story.
    /// </summary>
    public string TaxStatus { get; set; } = "none";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Navigation to the owning tenant.</summary>
    public Tenant? Tenant { get; set; }
}
