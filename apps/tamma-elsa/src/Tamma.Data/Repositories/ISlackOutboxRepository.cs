using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Story 38-3 — persistence port for the control-plane Slack notification
/// outbox (<see cref="SlackOutboxMessage"/>). The fire-and-forget analogue of
/// <see cref="IPlatformEmailOutboxRepository"/>: the mediation endpoint enqueues
/// intent and <c>OutboxSlackSender</c> claims-then-delivers with the same
/// reservation contract (<c>FOR UPDATE SKIP LOCKED</c> on Postgres).
/// </summary>
public interface ISlackOutboxRepository
{
    /// <summary>
    /// Insert a new message in <c>pending</c> state and return the persisted row.
    /// The returned <see cref="SlackOutboxMessage.Id"/> is the outbox id the
    /// endpoint returns and audit events tag.
    /// </summary>
    Task<SlackOutboxMessage> EnqueueAsync(SlackOutboxMessage msg, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the next <c>pending</c> row whose <c>NextAttemptAt</c> is
    /// at or before <paramref name="now"/>, flipping it to <c>sending</c>. Returns
    /// <c>null</c> when nothing is due. Postgres path uses <c>FOR UPDATE SKIP
    /// LOCKED</c> so concurrent senders are cluster-safe; the in-memory fallback is
    /// single-writer (test-only).
    /// </summary>
    Task<SlackOutboxMessage?> ClaimNextPendingAsync(DateTime now, CancellationToken ct = default);

    /// <summary>Mark a claimed message delivered (<c>Status=sent</c> + <c>SentAt</c>).</summary>
    Task MarkSentAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Record a transport failure. Increments <c>Attempts</c>; under the ceiling
    /// reschedules with <paramref name="backoff"/>; at the ceiling flips to
    /// <c>failed</c>. Returns the updated row so the caller can react to the
    /// terminal outcome. <paramref name="error"/> must already be key-free.
    /// </summary>
    Task<SlackOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default);

    /// <summary>Read by id, or <c>null</c>.</summary>
    Task<SlackOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Permanently remove a row.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
