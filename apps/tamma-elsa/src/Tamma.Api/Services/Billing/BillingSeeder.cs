using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Billing;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 (AC7) — idempotently syncs the Stripe catalog for every plan slug:
/// one Product + base Price per slug, three shared Billing Meters
/// (<c>tamma.platform_tokens_input</c> SUM, <c>tamma.platform_tokens_output</c>
/// SUM, <c>tamma.seats</c> LAST/gauge) created once and referenced by every
/// slug, and a metered Price per slug per meter. The ids are written into
/// <c>billing_plan_prices</c> (insert-if-absent / update-existing).
///
/// <para>Re-running is a no-op (AC7): the seeder reuses ids already stored in
/// <c>billing_plan_prices</c> (lookup-by-stored-id) and otherwise mints objects
/// with deterministic <see cref="RequestOptions.IdempotencyKey"/>s
/// (<c>billing-catalog-{slug}-{resource}</c>) so a retry never creates a
/// duplicate. The three meters are created once and shared — their event names
/// are globally unique so a second run reuses the stored ids.</para>
///
/// <para>Lives in <c>Tamma.Api</c> (not <c>Tamma.Data/Seeders</c>) because it
/// needs the Stripe SDK + the DCB event helper, neither of which the data layer
/// references. Plain class — not auto-run at startup; the <c>seed-billing</c>
/// CLI command is the only trigger (Stripe calls must be operator-initiated).</para>
/// </summary>
public sealed class BillingSeeder
{
    // The three platform-wide meter definitions (shared across all slugs).
    private const string TokensInputEventName = "tamma.platform_tokens_input";
    private const string TokensOutputEventName = "tamma.platform_tokens_output";
    private const string SeatsEventName = "tamma.seats";

    private static readonly string[] s_planSlugs = ["free", "team", "enterprise"];

    private readonly IStripeServices _stripe;
    private readonly ControlPlaneDbContext _db;
    private readonly IEventRepository _events;
    private readonly BillingOptions _options;
    private readonly ILogger _logger;

    public BillingSeeder(
        IStripeServices stripe,
        ControlPlaneDbContext db,
        IEventRepository events,
        BillingOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stripe);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _stripe = stripe;
        _db = db;
        _events = events;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Sync the catalog for all plan slugs. Returns per-slug created/reused
    /// counts; emits one <c>BILLING.PLAN_CATALOG.SYNCED</c> per slug.
    /// </summary>
    public async Task<CatalogSyncResult> SyncAsync(CancellationToken ct = default)
    {
        // Meters are platform-wide — create once, reuse across slugs. The first
        // existing catalog row that already carries meter ids supplies them; a
        // fresh install creates them once here.
        var (meters, metersCreated) = await EnsureMetersAsync(ct).ConfigureAwait(false);

        var slugResults = new List<CatalogSlugResult>();
        var first = true;
        foreach (var slug in s_planSlugs)
        {
            // Charge the one-time meter creation against the first slug's count
            // so the platform-wide objects are reflected somewhere.
            var meterCreateBudget = first ? metersCreated : 0;
            first = false;

            var result = await SyncSlugAsync(slug, meters, meterCreateBudget, ct)
                .ConfigureAwait(false);
            slugResults.Add(result);

            await _events.AppendAsync(
                BillingEvents.PlanCatalogSynced(slug, result.Created, result.Reused))
                .ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Billing catalog synced: {Slugs} slugs, created={Created}, reused={Reused}.",
            slugResults.Count, slugResults.Sum(s => s.Created), slugResults.Sum(s => s.Reused));

        return new CatalogSyncResult(slugResults);
    }

    /// <summary>
    /// Resolve the three shared meter ids — reuse ids already stored on any
    /// catalog row; otherwise create them once. Returns the meter ids + how many
    /// were newly created.
    /// </summary>
    private async Task<(MeterIds Ids, int Created)> EnsureMetersAsync(CancellationToken ct)
    {
        var anyRow = await _db.BillingPlanPrices
            .FirstOrDefaultAsync(
                r => r.TokensInputMeterId != null
                     && r.TokensOutputMeterId != null
                     && r.SeatsMeterId != null,
                ct)
            .ConfigureAwait(false);
        if (anyRow is not null)
        {
            return (new MeterIds(
                anyRow.TokensInputMeterId!,
                anyRow.TokensOutputMeterId!,
                anyRow.SeatsMeterId!), 0);
        }

        var tokensIn = await CreateMeterAsync(
            TokensInputEventName, "Tamma platform input tokens", "sum", ct).ConfigureAwait(false);
        var tokensOut = await CreateMeterAsync(
            TokensOutputEventName, "Tamma platform output tokens", "sum", ct).ConfigureAwait(false);
        var seats = await CreateMeterAsync(
            SeatsEventName, "Tamma seats", "last", ct).ConfigureAwait(false);

        return (new MeterIds(tokensIn, tokensOut, seats), 3);
    }

    private async Task<string> CreateMeterAsync(
        string eventName, string displayName, string formula, CancellationToken ct)
    {
        var options = new MeterCreateOptions
        {
            DisplayName = displayName,
            EventName = eventName,
            DefaultAggregation = new MeterDefaultAggregationOptions { Formula = formula },
        };
        // 'count' takes no value settings; 'sum'/'last' read a payload value.
        if (formula is "sum" or "last")
        {
            options.ValueSettings = new MeterValueSettingsOptions { EventPayloadKey = "value" };
        }

        var idempotencyKey = $"billing-catalog-meter-{eventName}";
        _logger.LogDebug(
            "Creating Stripe meter event={EventName} formula={Formula} (idempotencyKey set).",
            eventName, formula);

        var meter = await _stripe.Meters
            .CreateAsync(options, new RequestOptions { IdempotencyKey = idempotencyKey }, ct)
            .ConfigureAwait(false);
        return meter.Id;
    }

    private async Task<CatalogSlugResult> SyncSlugAsync(
        string slug, MeterIds meters, int meterCreateBudget, CancellationToken ct)
    {
        var row = await _db.BillingPlanPrices
            .FirstOrDefaultAsync(r => r.PlanSlug == slug, ct)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var isNewRow = row is null;
        row ??= new BillingPlanPrice
        {
            Id = Guid.NewGuid(),
            PlanSlug = slug,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = meterCreateBudget;
        var reused = 0;

        // Shared meter ids on every row (idempotent assignment).
        row.TokensInputMeterId = meters.TokensInput;
        row.TokensOutputMeterId = meters.TokensOutput;
        row.SeatsMeterId = meters.Seats;

        // Product — deterministic id so a re-run reuses it.
        if (string.IsNullOrEmpty(row.StripeProductId))
        {
            row.StripeProductId = await CreateProductAsync(slug, ct).ConfigureAwait(false);
            created++;
        }
        else
        {
            reused++;
        }

        // Base flat (per-seat / platform) recurring price.
        if (string.IsNullOrEmpty(row.StripePriceId))
        {
            row.StripePriceId = await CreateBasePriceAsync(slug, row.StripeProductId!, ct)
                .ConfigureAwait(false);
            created++;
        }
        else
        {
            reused++;
        }

        // Three metered prices (one per meter), each pointing at the shared meter.
        (row.TokensInputPriceId, var tic, var tir) = await EnsureMeteredPriceAsync(
            slug, "tokens-input", row.StripeProductId!, meters.TokensInput,
            row.TokensInputPriceId, ct).ConfigureAwait(false);
        created += tic; reused += tir;

        (row.TokensOutputPriceId, var toc, var tor) = await EnsureMeteredPriceAsync(
            slug, "tokens-output", row.StripeProductId!, meters.TokensOutput,
            row.TokensOutputPriceId, ct).ConfigureAwait(false);
        created += toc; reused += tor;

        (row.SeatsPriceId, var sc, var sr) = await EnsureMeteredPriceAsync(
            slug, "seats", row.StripeProductId!, meters.Seats,
            row.SeatsPriceId, ct).ConfigureAwait(false);
        created += sc; reused += sr;

        row.UpdatedAt = now;
        if (isNewRow) _db.BillingPlanPrices.Add(row);

        return new CatalogSlugResult(slug, created, reused);
    }

    private async Task<string> CreateProductAsync(string slug, CancellationToken ct)
    {
        var productId = $"tamma-plan-{slug}";

        // Get-or-create. The Product uses a FIXED id, so if the control-plane DB
        // was reset but Stripe still holds the product (and the 24h idempotency
        // window has lapsed), a bare CreateAsync would 400 "resource already
        // exists" instead of replaying. Probe by id first and reuse it if present;
        // only create when Stripe reports it absent (404 / resource_missing).
        try
        {
            var existing = await _stripe.Products.GetAsync(productId, null, null, ct)
                .ConfigureAwait(false);
            _logger.LogDebug(
                "Reusing existing Stripe product {ProductId} for slug {Slug}.", productId, slug);
            return existing.Id;
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Not present in Stripe — fall through to create.
        }

        var options = new ProductCreateOptions
        {
            Id = productId,
            Name = $"Tamma {char.ToUpperInvariant(slug[0])}{slug[1..]}",
            Description = $"Tamma {slug} plan",
            Metadata = new Dictionary<string, string> { ["tammaPlanSlug"] = slug },
        };
        var product = await _stripe.Products
            .CreateAsync(options,
                new RequestOptions { IdempotencyKey = $"billing-catalog-{slug}-product" }, ct)
            .ConfigureAwait(false);
        return product.Id;
    }

    private async Task<string> CreateBasePriceAsync(string slug, string productId, CancellationToken ct)
    {
        var options = new PriceCreateOptions
        {
            Product = productId,
            Currency = _options.DefaultCurrency,
            // Base price amount is owned by Story 34-1's price-book; 0 here is a
            // placeholder recurring line — this story wires the binding, not the
            // rate. Recurring monthly licensed seat line.
            UnitAmount = 0,
            Recurring = new PriceRecurringOptions
            {
                Interval = "month",
                UsageType = "licensed",
            },
            Nickname = $"tamma-{slug}-base",
            Metadata = new Dictionary<string, string> { ["tammaPlanSlug"] = slug },
        };
        var price = await _stripe.Prices
            .CreateAsync(options,
                new RequestOptions { IdempotencyKey = $"billing-catalog-{slug}-price-base" }, ct)
            .ConfigureAwait(false);
        return price.Id;
    }

    private async Task<(string Id, int Created, int Reused)> EnsureMeteredPriceAsync(
        string slug, string resource, string productId, string meterId,
        string? existingPriceId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(existingPriceId))
        {
            return (existingPriceId, 0, 1);
        }

        var options = new PriceCreateOptions
        {
            Product = productId,
            Currency = _options.DefaultCurrency,
            UnitAmount = 0,
            Recurring = new PriceRecurringOptions
            {
                Interval = "month",
                UsageType = "metered",
                Meter = meterId,
            },
            Nickname = $"tamma-{slug}-{resource}",
            Metadata = new Dictionary<string, string>
            {
                ["tammaPlanSlug"] = slug,
                ["tammaMeteredResource"] = resource,
            },
        };
        var price = await _stripe.Prices
            .CreateAsync(options,
                new RequestOptions { IdempotencyKey = $"billing-catalog-{slug}-price-{resource}" }, ct)
            .ConfigureAwait(false);
        return (price.Id, 1, 0);
    }

    private readonly record struct MeterIds(string TokensInput, string TokensOutput, string Seats);
}
