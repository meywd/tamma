using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 — the SaaS billing provider. Resolves the Stripe key through the
/// Epic 29 cabinet (via <see cref="IStripeServicesFactory"/>), maps each tenant
/// to a Stripe customer, and persists the <see cref="BillingCustomer"/> row.
/// Catalog sync is delegated to <see cref="BillingSeeder"/>.
///
/// <para>Idempotency is layered: a deterministic
/// <see cref="RequestOptions.IdempotencyKey"/> (<c>billing-customer-{tenantId}</c>)
/// makes the Stripe create itself replay-safe, and a lookup-by-stored-id before
/// the call means a second create for the same tenant returns the existing row
/// without a second Stripe call (AC12, AC13).</para>
/// </summary>
public sealed class StripeBillingProvider : IBillingProvider
{
    private readonly IStripeServicesFactory _stripeFactory;
    private readonly ControlPlaneDbContext _db;
    private readonly IEventRepository _events;
    private readonly BillingOptions _options;
    private readonly ILogger<StripeBillingProvider> _logger;

    public StripeBillingProvider(
        IStripeServicesFactory stripeFactory,
        ControlPlaneDbContext db,
        IEventRepository events,
        IOptions<BillingOptions> options,
        ILogger<StripeBillingProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(stripeFactory);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _stripeFactory = stripeFactory;
        _db = db;
        _events = events;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <summary>Deterministic Stripe idempotency key for a tenant's customer create.</summary>
    public static string CustomerIdempotencyKey(Guid tenantId) => $"billing-customer-{tenantId:D}";

    /// <inheritdoc />
    public async Task<BillingCustomer> CreateCustomerAsync(
        Guid tenantId, CustomerDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // Idempotency layer 1 — if a fully-acked row already exists for this
        // tenant, return it and make NO Stripe call (AC12).
        var existing = await _db.BillingCustomers
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (existing is not null && !string.IsNullOrEmpty(existing.StripeCustomerId))
        {
            _logger.LogDebug(
                "BillingCustomer already mapped for tenant {TenantId}; skipping Stripe create.",
                tenantId);
            return existing;
        }

        var stripe = await _stripeFactory.CreateAsync(ct).ConfigureAwait(false);

        var idempotencyKey = CustomerIdempotencyKey(tenantId);
        _logger.LogDebug(
            "Creating Stripe customer for tenant {TenantId} (idempotencyKey set).", tenantId);

        var options = new CustomerCreateOptions
        {
            Name = descriptor.TenantName,
            Email = descriptor.OwnerEmail,
            Description = $"Tamma tenant {descriptor.TenantSlug}",
            Metadata = new Dictionary<string, string>
            {
                ["tammaTenantId"] = tenantId.ToString("D"),
                ["tammaTenantSlug"] = descriptor.TenantSlug,
                ["billingMode"] = descriptor.Mode.ToString(),
            },
        };

        // Idempotency layer 2 — deterministic key so a retry never mints a
        // duplicate Stripe customer.
        var customer = await stripe.Customers
            .CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey }, ct)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        BillingCustomer row;
        if (existing is not null)
        {
            // A null-id retry row from a prior failed attempt — fill it in.
            existing.StripeCustomerId = customer.Id;
            existing.BillingMode = descriptor.Mode.ToString();
            existing.UpdatedAt = now;
            row = existing;
        }
        else
        {
            row = new BillingCustomer
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StripeCustomerId = customer.Id,
                BillingMode = descriptor.Mode.ToString(),
                DefaultCurrency = _options.DefaultCurrency,
                TaxStatus = "none",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.BillingCustomers.Add(row);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _events.AppendAsync(
            BillingEvents.CustomerCreated(tenantId, row.StripeCustomerId, row.BillingMode))
            .ConfigureAwait(false);

        _logger.LogInformation(
            "BillingCustomer created for tenant {TenantId} (stripeCustomerPresent={Present}, mode={Mode}).",
            tenantId, !string.IsNullOrEmpty(row.StripeCustomerId), row.BillingMode);

        return row;
    }

    /// <inheritdoc />
    public async Task<CatalogSyncResult> SyncCatalogAsync(CancellationToken ct = default)
    {
        var stripe = await _stripeFactory.CreateAsync(ct).ConfigureAwait(false);
        var seeder = new BillingSeeder(stripe, _db, _events, _options, _logger);
        return await seeder.SyncAsync(ct).ConfigureAwait(false);
    }
}
