using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the control-plane email outbox — system-scope
/// mail that must deliver before a tenant DB exists (registration
/// verification, password reset, welcome) or after one is gone (tenant
/// deletion confirmation). The CP analogue of
/// <see cref="IEmailOutboxRepository"/>.
///
/// <para>Reservation contract is identical to the tenant outbox so
/// <c>OutboxSmtpSender</c> can drain both queues with the same
/// claim-then-deliver loop.</para>
/// </summary>
public interface IPlatformEmailOutboxRepository
{
    /// <summary>
    /// Insert a new message in <c>pending</c> state and return the
    /// persisted row. The returned <see cref="PlatformEmailOutboxMessage.Id"/>
    /// is the transaction id callers log and tag domain events with.
    /// </summary>
    Task<PlatformEmailOutboxMessage> EnqueueAsync(
        PlatformEmailOutboxMessage msg, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the next <c>pending</c> row whose
    /// <c>NextAttemptAt</c> is at or before <paramref name="now"/>,
    /// flipping it to <c>sending</c>. Returns <c>null</c> when nothing
    /// is due. Postgres path uses <c>FOR UPDATE SKIP LOCKED</c> so
    /// concurrent senders are cluster-safe; in-memory fallback is
    /// single-writer (test-only).
    /// </summary>
    Task<PlatformEmailOutboxMessage?> ClaimNextPendingAsync(
        DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Mark a claimed message as successfully delivered. Sets
    /// <c>Status=sent</c> + <c>SentAt=UtcNow</c>.
    /// </summary>
    Task MarkSentAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Record a transport failure. Increments <c>Attempts</c>; under
    /// the ceiling reschedules with the supplied
    /// <paramref name="backoff"/>; at the ceiling flips to
    /// <c>failed</c>. Returns the updated row so the caller can react
    /// to the terminal outcome.
    /// </summary>
    Task<PlatformEmailOutboxMessage?> MarkFailedAsync(
        Guid id, string error, TimeSpan? backoff, CancellationToken ct = default);

    /// <summary>
    /// Story 28-5 AC2 step-10 + AC5 — idempotently enqueue the per-tenant
    /// welcome email (<c>Template='welcome'</c>) into the control-plane
    /// outbox. Exactly-once-per-tenant: if a non-<c>failed</c> welcome row
    /// already exists for <paramref name="tenantId"/> the existing row is
    /// returned unchanged (workflow replay safety). A prior <c>failed</c>
    /// welcome does NOT block a fresh enqueue — the partial unique index
    /// <c>(TenantId, Template) WHERE Status &lt;&gt; 'failed'</c> backs this
    /// in Postgres, and the in-code pre-check covers the in-memory path.
    ///
    /// <para>The body is rendered from the standard welcome copy keyed on
    /// <paramref name="tenantName"/>; <paramref name="toAddress"/> is the
    /// tenant owner's verified email and <paramref name="fromAddress"/> the
    /// platform <c>Email:From</c> sender.</para>
    /// </summary>
    Task<PlatformEmailOutboxMessage> EnqueueWelcomeOnceAsync(
        Guid tenantId,
        string toAddress,
        string tenantName,
        string fromAddress,
        CancellationToken ct = default);

    /// <summary>Read by id, or <c>null</c>.</summary>
    Task<PlatformEmailOutboxMessage?> GetByIdAsync(
        Guid id, CancellationToken ct = default);

    /// <summary>Permanently remove a row (post-send cleanup).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
