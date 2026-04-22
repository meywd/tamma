using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

public sealed class GitHubWebhookDeliveryRepository : IGitHubWebhookDeliveryRepository
{
    private readonly TammaDbContext _db;

    public GitHubWebhookDeliveryRepository(TammaDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryRecordAsync(
        Guid deliveryId,
        string eventType,
        string? action,
        long? installationId,
        CancellationToken ct = default)
    {
        // Cheap precheck — the unique PK still backstops the race below.
        if (await _db.GitHubWebhookDeliveries
                .AnyAsync(d => d.DeliveryId == deliveryId, ct))
        {
            return false;
        }

        _db.GitHubWebhookDeliveries.Add(new GitHubWebhookDelivery
        {
            DeliveryId = deliveryId,
            ReceivedAt = DateTime.UtcNow,
            EventType = eventType,
            Action = action,
            InstallationId = installationId
        });

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Concurrent insert won — treat as duplicate so the caller skips
            // dispatch. The other request is responsible for processing.
            return false;
        }
    }

    public async Task<int> CleanupOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        return await _db.GitHubWebhookDeliveries
            .Where(d => d.ReceivedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
