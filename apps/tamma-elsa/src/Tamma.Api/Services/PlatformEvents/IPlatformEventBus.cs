using Tamma.Data.Entities;

namespace Tamma.Api.Services.PlatformEvents;

/// <summary>
/// Asynchronous handler invoked by <see cref="IPlatformEventBus"/> after a
/// <see cref="PlatformEvent"/> has been successfully persisted to the
/// control-plane event log. Handlers run sequentially in publication
/// order; exceptions are caught by the bus and logged, never propagated to
/// the publisher (a misbehaving cache-invalidation handler must not
/// abort the workflow that emitted the event).
/// </summary>
/// <param name="evt">The persisted event. Treat as immutable.</param>
/// <param name="ct">A cancellation token tied to the host's shutdown.</param>
public delegate Task PlatformEventHandler(PlatformEvent evt, CancellationToken ct);

/// <summary>
/// In-process publish-subscribe seam for control-plane lifecycle events.
/// Subscribers are typically background services (cache invalidators,
/// analytics rollups, dashboard fan-outs) that need to react to a
/// <see cref="PlatformEvent"/> immediately after it is appended to the
/// log without polling the table.
///
/// <para>Lifetime: singleton. Handlers register at composition root via
/// <see cref="Subscribe(PlatformEventHandler)"/> or
/// <see cref="Subscribe(string, PlatformEventHandler)"/> (type-prefix
/// filter) and remain active until the returned
/// <see cref="IDisposable"/> is disposed or the host stops.</para>
///
/// <para>Delivery contract:
/// <list type="bullet">
///   <item><description>Publication is fire-and-forget from the
///     publisher's perspective — <see cref="PublishAsync"/> returns once
///     handlers have all completed (or thrown, in which case the bus
///     swallows + logs).</description></item>
///   <item><description>Handlers are invoked sequentially in
///     subscription order. A slow handler delays subsequent handlers
///     for the same event but never blocks future publishes.</description></item>
///   <item><description>Process-local. Multi-pod deployments need a
///     real pub/sub bridge (Postgres LISTEN/NOTIFY against
///     <c>platform_events</c>, or Redis) before cross-pod subscribers
///     work. This is the same trade-off
///     <c>InMemoryEngineLifecycleBus</c> documents.</description></item>
/// </list></para>
///
/// <para>Story 28-6 §AC4 publishers: <see cref="IPlatformEventRepository"/>
/// callers may either invoke the bus inline after a successful append, or
/// route the append + publish through this bus's
/// <see cref="AppendAndPublishAsync"/> convenience seam to keep
/// publication coupled to durable persistence in one place.</para>
/// </summary>
public interface IPlatformEventBus
{
    /// <summary>
    /// Subscribe to every event regardless of type. Returns a token that
    /// removes the handler from the subscription list when disposed.
    /// </summary>
    IDisposable Subscribe(PlatformEventHandler handler);

    /// <summary>
    /// Subscribe to events whose <see cref="PlatformEvent.Type"/> starts
    /// with <paramref name="typePrefix"/> (case-sensitive). Pass
    /// <c>"TENANT."</c> to receive every tenant-lifecycle event,
    /// <c>"USER.LOGIN."</c> to receive successes + failures, etc.
    /// Returns a token that removes the handler when disposed.
    /// </summary>
    IDisposable Subscribe(string typePrefix, PlatformEventHandler handler);

    /// <summary>
    /// Publish an already-persisted event to all matching subscribers.
    /// Awaits handlers sequentially; per-handler exceptions are caught
    /// and logged so a buggy subscriber never breaks publication.
    /// </summary>
    Task PublishAsync(PlatformEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Convenience: append <paramref name="evt"/> via the supplied
    /// repository and, on success (non-null return — i.e. not a dedup
    /// no-op), publish it to subscribers. Returns the persisted event
    /// or <c>null</c> when the append was a dedup no-op (no publish in
    /// that case — subscribers already saw the original).
    /// </summary>
    Task<PlatformEvent?> AppendAndPublishAsync(
        Tamma.Data.Repositories.IPlatformEventRepository repository,
        PlatformEvent evt,
        CancellationToken ct = default);

    /// <summary>
    /// Current number of registered subscribers. Exposed for tests +
    /// observability; not consumed by endpoints.
    /// </summary>
    int SubscriberCount { get; }
}
