using Tamma.Core.Billing;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 — the single billing seam. In SaaS this is
/// <see cref="StripeBillingProvider"/> (creates Stripe customers, syncs the
/// catalog); in single-user it is <see cref="NullBillingProvider"/>
/// (<see cref="IsEnabled"/> = false, no Stripe calls, no rows). DI picks one
/// based on <c>ITammaModeProvider.Mode</c>.
/// </summary>
public interface IBillingProvider
{
    /// <summary>
    /// True only when billing is active (SaaS). The tenant-create hook and the
    /// seed command short-circuit when this is false — single-user makes ZERO
    /// Stripe calls.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Create (or resolve the existing) Stripe customer for a tenant and
    /// persist the <see cref="BillingCustomer"/> mapping. Idempotent: a second
    /// call for the same tenant resolves the existing row (no duplicate Stripe
    /// customer) via the unique <c>TenantId</c> + a deterministic idempotency
    /// key. Throws on a single-user (disabled) provider.
    /// </summary>
    Task<BillingCustomer> CreateCustomerAsync(
        Guid tenantId, CustomerDescriptor descriptor, CancellationToken ct = default);

    /// <summary>
    /// Idempotently sync the Stripe catalog (Products, base + metered Prices,
    /// the three Billing Meters) for every plan slug and write the ids into
    /// <c>billing_plan_prices</c>. Re-running is a no-op (existing ids reused).
    /// </summary>
    Task<CatalogSyncResult> SyncCatalogAsync(CancellationToken ct = default);
}

/// <summary>
/// The tenant-shaped inputs for a Stripe customer create. No payment details —
/// only identifying metadata that is safe to log at DEBUG.
/// </summary>
public sealed record CustomerDescriptor(
    string TenantName,
    string TenantSlug,
    string? OwnerEmail,
    BillingMode Mode);

/// <summary>Per-slug catalog sync outcome (counts of created vs reused Stripe objects).</summary>
public sealed record CatalogSyncResult(IReadOnlyList<CatalogSlugResult> Slugs)
{
    public int TotalCreated => Slugs.Sum(s => s.Created);
    public int TotalReused => Slugs.Sum(s => s.Reused);
}

/// <summary>One slug's catalog sync result.</summary>
public sealed record CatalogSlugResult(string PlanSlug, int Created, int Reused);
