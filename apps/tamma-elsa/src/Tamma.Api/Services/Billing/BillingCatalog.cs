using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-1 — EF-backed <see cref="IBillingCatalog"/> with a short in-process
/// cache. The catalog is platform-global and slug-keyed, so a process-wide cache
/// is safe (no per-tenant leakage). Entries expire after a minute so a fresh
/// <c>seed-billing</c> run is observed promptly.
/// </summary>
public sealed class BillingCatalog : IBillingCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<ControlPlaneDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.Ordinal);

    public BillingCatalog(
        IDbContextFactory<ControlPlaneDbContext> dbFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<BillingPlanPrice> GetBySlugAsync(
        string planSlug, CancellationToken ct = default)
    {
        var row = await TryGetBySlugAsync(planSlug, ct).ConfigureAwait(false);
        if (row is null)
        {
            throw new TammaError(
                "BILLING.CATALOG.UNKNOWN_SLUG",
                $"No billing catalog row for plan slug '{planSlug}'. "
                + "Run `seed-billing` to populate the Stripe catalog, or check the slug.",
                new Dictionary<string, object?> { ["planSlug"] = planSlug },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
        return row;
    }

    /// <inheritdoc />
    public async Task<BillingPlanPrice?> TryGetBySlugAsync(
        string planSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planSlug);

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(planSlug, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Row;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.BillingPlanPrices
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PlanSlug == planSlug, ct)
            .ConfigureAwait(false);

        _cache[planSlug] = new CacheEntry(row, now.Add(CacheTtl));
        return row;
    }

    private readonly record struct CacheEntry(BillingPlanPrice? Row, DateTimeOffset ExpiresAt);
}
