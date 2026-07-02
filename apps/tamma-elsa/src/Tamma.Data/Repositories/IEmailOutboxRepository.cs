using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the per-tenant email store-and-forward outbox.
/// Pure CRUD; scheduling policy (poll cadence, backoff curve) lives in
/// the <c>OutboxSmtpSender</c> hosted service.
///
/// <para>Story 28-1 PR B — this repository is now <b>strictly
/// tenant-scoped</b>. Every operation requires a <c>tenantId</c> so the
/// implementation can route to the correct per-tenant DB. Callers that
/// have no tenant (welcome / verification / password reset emails sent
/// before a tenant DB exists) must use
/// <see cref="IPlatformEmailOutboxRepository"/> instead. The decision
/// matrix is in <c>.dev/decisions/story-28-1-design-calls.md</c> §5.</para>
///
/// <para>The platform table (<c>platform_email_outbox</c>) and the
/// per-tenant table (<c>email_outbox</c>) physically co-reside on
/// the control-plane DB until PR D moves the tenant table into per-tenant
/// DBs. The split here is purely <em>logical</em>: PR B routes each
/// caller to the right repo so PR D's physical move is mechanical.</para>
/// </summary>
public interface IEmailOutboxRepository
{
    /// <summary>
    /// Insert a new message in <c>pending</c> state into the tenant's
    /// outbox and return the persisted row. The returned
    /// <see cref="EmailOutboxMessage.Id"/> is the transaction id callers
    /// log and tag domain events with.
    /// <para>Throws <see cref="ArgumentException"/> if
    /// <see cref="EmailOutboxMessage.TenantId"/> is null — platform-scope
    /// callers must use <see cref="IPlatformEmailOutboxRepository"/>.</para>
    /// </summary>
    Task<EmailOutboxMessage> EnqueueAsync(EmailOutboxMessage msg, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the next <c>pending</c> row for the supplied
    /// tenant whose <c>NextAttemptAt</c> is at or before
    /// <paramref name="now"/>, flipping it to <c>sending</c> so no other
    /// sender can pick it up. Returns <c>null</c> when nothing is due
    /// for that tenant.
    /// <para>Postgres implementation uses <c>FOR UPDATE SKIP LOCKED</c>
    /// so concurrent senders are cluster-safe; the provider-independent
    /// in-memory fallback relies on change-tracking semantics
    /// (single-writer, adequate for tests).</para>
    /// </summary>
    Task<EmailOutboxMessage?> ClaimNextPendingAsync(
        Guid tenantId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant drain pass — list active tenants from the CP
    /// <c>tenants</c> table and try
    /// <see cref="ClaimNextPendingAsync"/> on each until one returns a
    /// row, or every tenant has been visited. Returns the claimed row
    /// (with its <see cref="EmailOutboxMessage.TenantId"/> populated for
    /// the caller's subsequent mark-sent / mark-failed calls), or
    /// <c>null</c> when no tenant has work.
    /// <para>Used by <c>OutboxSmtpSender</c> to drain the tenant outbox
    /// queues without holding a static enumeration of tenants. The
    /// list-active-tenants query is bounded by the <c>tenants</c> table
    /// size and runs at the configured poll cadence — cheap relative to
    /// the SMTP delivery cost it precedes.</para>
    /// </summary>
    Task<EmailOutboxMessage?> ClaimNextPendingFromAnyTenantAsync(
        DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Durability reaper (single tenant) — reset rows orphaned in
    /// <c>sending</c> back to <c>pending</c> for the supplied tenant so the
    /// sender re-claims and re-delivers them. Claiming a row flips it to
    /// <c>sending</c> (stamping <c>UpdatedAt</c>); if the process crashes
    /// before <see cref="MarkSentAsync"/> / <see cref="MarkFailedAsync"/> the
    /// row is orphaned forever (never re-selected by
    /// <see cref="ClaimNextPendingAsync"/>), defeating at-least-once delivery.
    /// A row qualifies when its <c>Status='sending'</c> and its <c>UpdatedAt</c>
    /// (stamped at claim time) is older than <paramref name="leaseTimeout"/>
    /// before <paramref name="now"/>. <see cref="EmailOutboxMessage.Attempts"/>
    /// is deliberately NOT incremented — a stuck row was never
    /// attempted-to-completion, so it keeps its full retry budget. Returns the
    /// number of rows reclaimed for that tenant.
    /// </summary>
    Task<int> ReclaimStuckSendingAsync(
        Guid tenantId, DateTime now, TimeSpan leaseTimeout, CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant reaper — enumerate active tenants from the CP
    /// <c>tenants</c> table and run <see cref="ReclaimStuckSendingAsync"/> on
    /// each, returning the total number of rows reclaimed. The tenant analogue
    /// of <see cref="ClaimNextPendingFromAnyTenantAsync"/>; used by
    /// <c>OutboxSmtpSender</c> to reap orphaned <c>sending</c> rows across the
    /// per-tenant outbox queues before each claim pass. A per-tenant failure
    /// (mid-deletion, transient connection error) is swallowed so one tenant's
    /// outage does not block the rest; cooperative cancellation propagates.
    /// </summary>
    Task<int> ReclaimStuckSendingFromAllTenantsAsync(
        DateTime now, TimeSpan leaseTimeout, CancellationToken ct = default);

    /// <summary>
    /// Mark a claimed message as successfully delivered. Sets
    /// <c>Status=sent</c> and <c>SentAt=UtcNow</c>.
    /// </summary>
    Task MarkSentAsync(Guid tenantId, Guid id, CancellationToken ct = default);

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
        Guid tenantId, Guid id, string error, TimeSpan? backoff, CancellationToken ct = default);

    /// <summary>Load a single row by id, or <c>null</c> if missing.</summary>
    Task<EmailOutboxMessage?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Permanently remove a row. Used by the sender after a successful delivery
    /// so recipient address, subject, and body don't linger in the DB — the
    /// audit trail lives in the event store (<c>EMAIL.SENT.SUCCESS</c>)
    /// which holds only txn id + template metadata. Failed rows are kept for
    /// operator inspection and are NOT deleted by this method.
    /// </summary>
    Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default);
}
