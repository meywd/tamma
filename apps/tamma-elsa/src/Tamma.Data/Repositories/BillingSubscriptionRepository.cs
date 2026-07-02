using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 35-4 — EF-backed <see cref="IBillingSubscriptionRepository"/> over
/// <see cref="ControlPlaneDbContext"/>. Reads are tenant-scoped; the terminal
/// statuses (<c>canceled</c>, <c>incomplete_expired</c>) are excluded from the
/// "active" lookup so a fresh subscription after a prior cancellation resolves
/// cleanly (mirrors the partial-unique index filter).
/// </summary>
public sealed class BillingSubscriptionRepository : IBillingSubscriptionRepository
{
    private static readonly string[] TerminalStatuses = { "canceled", "incomplete_expired" };

    private readonly ControlPlaneDbContext _db;

    public BillingSubscriptionRepository(ControlPlaneDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<BillingSubscription?> GetActiveByTenantAsync(
        Guid tenantId, CancellationToken ct = default) =>
        await _db.BillingSubscriptions
            .Where(s => s.TenantId == tenantId && !TerminalStatuses.Contains(s.Status))
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<BillingSubscription?> GetByStripeSubscriptionIdAsync(
        string stripeSubscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeSubscriptionId);
        return await _db.BillingSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(BillingSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await _db.BillingSubscriptions.AddAsync(subscription, ct).ConfigureAwait(false);
    }
}
