using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;
using Tamma.Platforms.Abstractions;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 31-7 — EF-Core implementation of
/// <see cref="IPlatformWebhookDeliveryRepository"/>. Mirrors the
/// patterns of <see cref="GitHubWebhookDeliveryRepository"/>: cheap
/// pre-check followed by INSERT-with-catch-DbUpdateException so
/// concurrent inserts collapse to a single accepted delivery and the
/// loser sees the duplicate path cleanly.
/// </summary>
public sealed class PlatformWebhookDeliveryRepository : IPlatformWebhookDeliveryRepository
{
    private readonly ControlPlaneDbContext _db;

    public PlatformWebhookDeliveryRepository(ControlPlaneDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordAsync(
        PlatformKind platformKind,
        string deliveryId,
        string? eventType,
        string? installationExternalId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        var wireKind = PlatformKindWire.ToWire(platformKind);

        // Cheap precheck — partial unique index still backstops the
        // race below (two concurrent receivers seeing the same
        // delivery id from a webhook retry).
        if (await _db.PlatformWebhookDeliveries
                .AsNoTracking()
                .AnyAsync(d => d.PlatformKind == wireKind && d.DeliveryId == deliveryId, ct)
                .ConfigureAwait(false))
        {
            return false;
        }

        _db.PlatformWebhookDeliveries.Add(new PlatformWebhookDelivery
        {
            PlatformKind = wireKind,
            DeliveryId = deliveryId,
            EventType = eventType,
            InstallationExternalId = installationExternalId,
            ReceivedAt = DateTime.UtcNow,
        });

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Concurrent insert won — treat as duplicate so the caller
            // skips dispatch. The other request is responsible for
            // processing.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanupOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        return await _db.PlatformWebhookDeliveries
            .Where(d => d.ReceivedAt < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
