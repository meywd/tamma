namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-7 AC5 / AC6 — neutral handler contract for inbound webhook
/// events. Handlers register with the dispatcher under a
/// (<see cref="PlatformKind"/>, event-pattern) tuple and are invoked
/// once per matching <see cref="PlatformWebhookEvent"/>.
///
/// <para><b>Lifetime</b>: scoped per dispatch — the dispatcher pulls a
/// fresh <see cref="IServiceScope"/> when it needs DB / repo access. A
/// handler instance is single-use; do not stash mutable state on the
/// handler.</para>
///
/// <para><b>Failure isolation</b>: a thrown exception from
/// <see cref="HandleAsync"/> is caught by the dispatcher, logged, and
/// emitted as a <c>PLATFORM.WEBHOOK.HANDLER_FAILED</c> control-plane
/// event. The receiver still returns 200 to the platform — webhook
/// senders re-deliver on 5xx and a buggy handler must not trigger a
/// re-delivery storm.</para>
///
/// <para><b>Cross-tenant invariant</b>: a handler MUST scope every DB
/// read by <see cref="PlatformWebhookEvent.TenantId"/> when it's
/// non-null. The dispatcher already filtered by
/// (<see cref="PlatformKind"/>, event-pattern); the handler's own
/// queries must not widen that scope.</para>
/// </summary>
public interface IWebhookHandler
{
    /// <summary>
    /// The platform this handler reacts to. The dispatcher checks
    /// <c>handler.Kind == evt.Kind</c> as a defence against keyed-DI
    /// mis-registration.
    /// </summary>
    PlatformKind Kind { get; }

    /// <summary>
    /// Event-type pattern this handler matches. Two shapes:
    /// <list type="bullet">
    ///   <item>Exact: <c>"installation.created"</c> matches the GitHub
    ///         <c>installation</c> event with <c>action=created</c>
    ///         (the dispatcher composes
    ///         <c>{eventType}.{action}</c> for matching when
    ///         <see cref="PlatformWebhookEvent.Action"/> is non-null).</item>
    ///   <item>Wildcard: <c>"installation.*"</c> matches every
    ///         <c>installation</c> action.</item>
    /// </list>
    /// A bare event type without an action (e.g. <c>"push"</c>) matches
    /// the event regardless of action. The dispatcher's pattern matcher
    /// is documented on <see cref="IWebhookEventDispatcher"/>.
    /// </summary>
    string EventTypePattern { get; }

    /// <summary>
    /// Process the event. Implementations should be idempotent —
    /// duplicates are filtered earlier (idempotency table) but a
    /// handler is the last line of defence on at-most-once side
    /// effects.
    /// </summary>
    Task HandleAsync(PlatformWebhookEvent evt, CancellationToken ct = default);
}
