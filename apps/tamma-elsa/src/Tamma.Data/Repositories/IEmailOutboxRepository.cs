using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the email store-and-forward outbox. Pure CRUD;
/// scheduling policy (poll cadence, backoff curve) lives in the
/// <c>OutboxSmtpSender</c> hosted service.
/// </summary>
public interface IEmailOutboxRepository
{
    /// <summary>
    /// Insert a new message in <c>pending</c> state and return the persisted row.
    /// The returned <see cref="EmailOutboxMessage.Id"/> is the transaction id
    /// callers log and tag domain events with.
    /// </summary>
    Task<EmailOutboxMessage> EnqueueAsync(EmailOutboxMessage msg, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the next <c>pending</c> row whose <c>NextAttemptAt</c>
    /// is at or before <paramref name="now"/>, flipping it to <c>sending</c> so
    /// no other sender can pick it up. Returns <c>null</c> when nothing is due.
    /// <para>
    /// Postgres implementation uses <c>FOR UPDATE SKIP LOCKED</c> so concurrent
    /// senders are cluster-safe; the provider-independent in-memory fallback
    /// relies on change-tracking semantics (single-writer, adequate for tests).
    /// </para>
    /// </summary>
    Task<EmailOutboxMessage?> ClaimNextPendingAsync(DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Mark a claimed message as successfully delivered. Sets <c>Status=sent</c>
    /// and <c>SentAt=UtcNow</c>.
    /// </summary>
    Task MarkSentAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Record a transport failure.
    /// <list type="bullet">
    ///   <item><description>Increments <see cref="EmailOutboxMessage.Attempts"/>.</description></item>
    ///   <item><description>Stores <paramref name="error"/> in
    ///     <see cref="EmailOutboxMessage.LastError"/>.</description></item>
    ///   <item><description>If <see cref="EmailOutboxMessage.Attempts"/> &lt;
    ///     <see cref="EmailOutboxMessage.MaxAttempts"/> — flips back to
    ///     <c>pending</c> and schedules the next attempt at
    ///     <c>UtcNow + <paramref name="backoff"/></c> (defaulted to 1 minute
    ///     when <paramref name="backoff"/> is null).</description></item>
    ///   <item><description>Otherwise — flips to <c>failed</c>.</description></item>
    /// </list>
    /// Returns the updated row so the caller knows whether the retry ceiling was hit.
    /// </summary>
    Task<EmailOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default);

    /// <summary>Load a single row by id, or <c>null</c> if missing.</summary>
    Task<EmailOutboxMessage?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
