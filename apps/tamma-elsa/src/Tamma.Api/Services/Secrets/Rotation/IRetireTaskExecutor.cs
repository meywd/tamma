namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC8 — performs the side-effecting part of retiring ONE
/// secret version: fetch the old plaintext, flip
/// <c>RetiredGrace → Revoked</c> via the gateway, best-effort invoke the
/// handler's <c>RevokeOldAsync</c> hook, and emit
/// <c>SECRET.VERSION.RETIRED</c>. Deliberately does NOT touch the queue
/// row's state — the two drainers own their own bookkeeping:
///
/// <list type="bullet">
///   <item><description><c>RetireSecretVersionTaskHandler</c> (the AC8
///     <c>PlatformTaskWorker</c> route) lets the worker mark
///     completed / failed / dead-letter from the handler's return /
///     thrown exception.</description></item>
///   <item><description><see cref="RetireScheduler.SweepDueRetireTasksAsync"/>
///     (the periodic fallback) reserves + completes / fails the row
///     itself.</description></item>
/// </list>
/// </summary>
public interface IRetireTaskExecutor
{
    /// <summary>
    /// Retire the version referenced by <paramref name="payload"/>.
    /// Idempotent — an already-<c>Revoked</c> version is a no-op (the
    /// gateway short-circuits). A throwing <c>RevokeOldAsync</c> is
    /// logged and swallowed (best-effort) so the store-side revocation
    /// is not undone by a downstream cleanup hiccup. Any OTHER failure
    /// (gateway throw) propagates so the caller can decide retry vs
    /// dead-letter.
    /// </summary>
    /// <param name="payload">Parsed retire payload (secret id, version,
    /// correlation id).</param>
    /// <param name="taskId">Queue row id — recorded on the emitted
    /// <c>SECRET.VERSION.RETIRED</c> event for traceability.</param>
    Task RetireOneAsync(RetireTaskPayload payload, Guid taskId, CancellationToken ct);
}
