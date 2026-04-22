namespace Tamma.Data.Repositories;

/// <summary>
/// Idempotency journal for GitHub webhook deliveries. Audit findings
/// 003 + 019.
/// </summary>
public interface IGitHubWebhookDeliveryRepository
{
    /// <summary>
    /// Atomically insert a delivery record. Returns <c>true</c> if this is
    /// the first time we've seen <paramref name="deliveryId"/>, <c>false</c>
    /// if a row already exists (i.e. this is a redelivery and the caller
    /// should skip dispatch).
    /// </summary>
    Task<bool> TryRecordAsync(
        Guid deliveryId,
        string eventType,
        string? action,
        long? installationId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete delivery rows older than <paramref name="cutoff"/>. Returns
    /// the number of rows pruned. Intended for periodic background cleanup.
    /// </summary>
    Task<int> CleanupOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
