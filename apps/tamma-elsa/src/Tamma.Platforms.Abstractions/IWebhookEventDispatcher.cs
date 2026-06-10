namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-7 AC5 — fans out a verified
/// <see cref="PlatformWebhookEvent"/> to the registered
/// <see cref="IWebhookHandler"/>s.
///
/// <para><b>Single-handler-per-event invariant</b>: this story ships
/// with a single handler bound per
/// <c>(<see cref="PlatformKind"/>, eventTypePattern)</c> tuple — the
/// first registration wins; subsequent registrations under the same key
/// throw at registration time. Multi-handler dispatch is intentionally
/// deferred — the next story in the epic that needs it will widen the
/// contract.</para>
///
/// <para><b>Pattern matching</b>: handler patterns map via the rules
/// documented on <see cref="IWebhookHandler.EventTypePattern"/>:
/// <list type="bullet">
///   <item>Exact <c>"installation.created"</c> matches an event whose
///         <c>{eventType}.{action}</c> composition equals the pattern.</item>
///   <item>Wildcard <c>"installation.*"</c> matches every action under
///         the same event type.</item>
///   <item>Bare event type <c>"push"</c> (no dot) matches the event
///         regardless of action — pushes don't carry one anyway.</item>
/// </list>
/// Patterns are case-sensitive. The dispatcher picks the most specific
/// matching handler (exact wins over wildcard) so a registration of
/// <c>"installation.created"</c> + <c>"installation.*"</c> under the
/// same kind is accepted (different patterns, both legal); the
/// dispatcher routes <c>installation.created</c> to the exact match
/// and <c>installation.deleted</c> to the wildcard.</para>
///
/// <para><b>Threading</b>: <see cref="DispatchAsync"/> is fire-and-forget
/// from the receiver's perspective — the dispatcher schedules the
/// handler on the thread pool and the receiver returns 200 immediately.
/// Failures are caught and logged (see
/// <see cref="IWebhookHandler"/> contract docs).</para>
///
/// <para><b>Cross-tenant safety</b>: a handler can only see events
/// whose <see cref="PlatformWebhookEvent.Kind"/> matches its
/// <see cref="IWebhookHandler.Kind"/>. The receiver populates
/// <see cref="PlatformWebhookEvent.TenantId"/> via
/// <see cref="IPlatformResolver.ResolveForWebhookAsync"/> before
/// dispatch; cross-tenant leakage requires a handler bug
/// (it widens its own DB scope) — the dispatcher guarantees no event
/// for tenant B reaches a handler whose pattern matched only because
/// of tenant A's installation.</para>
/// </summary>
public interface IWebhookEventDispatcher
{
    /// <summary>
    /// Register a handler for the
    /// (<see cref="IWebhookHandler.Kind"/>,
    /// <see cref="IWebhookHandler.EventTypePattern"/>) pair. Throws
    /// <see cref="InvalidOperationException"/> if a handler is already
    /// registered under the same key.
    /// </summary>
    void RegisterHandler(IWebhookHandler handler);

    /// <summary>
    /// Dispatch an event to its matching handler (if any). Returns the
    /// number of handlers invoked — 0 when no handler matched, 1 when
    /// the single-handler invariant was satisfied. The returned task
    /// completes once the handler completes (success or caught failure);
    /// the receiver normally awaits this fire-and-forget (calling code
    /// detaches via <c>Task.Run</c> when it doesn't want to block the
    /// HTTP response).
    /// </summary>
    Task<int> DispatchAsync(
        PlatformWebhookEvent evt,
        CancellationToken ct = default);

    /// <summary>
    /// Snapshot count of registered handlers — exposed for tests +
    /// diagnostics; not consumed by request paths.
    /// </summary>
    int HandlerCount { get; }
}
