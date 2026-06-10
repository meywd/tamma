using Tamma.Platforms.Abstractions;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 31-7 — cross-platform idempotency journal for inbound webhook
/// deliveries. Generalises <see cref="IGitHubWebhookDeliveryRepository"/>
/// across every <see cref="PlatformKind"/> the receiver accepts.
///
/// <para>The receiver consults the journal BEFORE dispatching a
/// verified event. A non-null <c>(platformKind, deliveryId)</c> hashes
/// against the partial unique index; duplicates are signalled to the
/// caller via a <c>false</c> return so the receiver returns 200 without
/// re-dispatching.</para>
/// </summary>
public interface IPlatformWebhookDeliveryRepository
{
    /// <summary>
    /// Atomically insert a delivery record. Returns <c>true</c> if
    /// this is the first time we've seen
    /// <paramref name="deliveryId"/> for <paramref name="platformKind"/>,
    /// <c>false</c> if a row already exists (i.e. this is a redelivery
    /// and the caller should skip dispatch).
    /// </summary>
    Task<bool> TryRecordAsync(
        PlatformKind platformKind,
        string deliveryId,
        string? eventType,
        string? installationExternalId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete delivery rows older than <paramref name="cutoff"/>. Returns
    /// the number of rows pruned. Intended for periodic background
    /// cleanup.
    /// </summary>
    Task<int> CleanupOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
