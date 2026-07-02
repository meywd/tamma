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
    // Declared as IReadOnlyList<string> (NOT string[]) on purpose: under C# 13's
    // first-class-span overload resolution, `array.Contains(x)` binds to
    // MemoryExtensions.Contains(ReadOnlySpan<T>, T) via the implicit array→span
    // conversion. EF Core's LINQ interpreter then tries to use ReadOnlySpan<string>
    // as a generic argument and throws TypeLoadException (a ref struct can't be a
    // generic type arg). The interface receiver has no array→span conversion, so the
    // call binds to Enumerable.Contains, which EF translates cleanly to SQL `IN`.
    private static readonly IReadOnlyList<string> TerminalStatuses =
        new[] { "canceled", "incomplete_expired" };

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
