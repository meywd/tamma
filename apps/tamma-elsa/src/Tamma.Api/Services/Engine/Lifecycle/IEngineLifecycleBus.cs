namespace Tamma.Api.Services.Engine.Lifecycle;

/// <summary>
/// A single frame published through the engine lifecycle bus.
/// </summary>
/// <param name="Type">
/// Event type in dotted-path form, e.g. <c>workflow.started</c>,
/// <c>engine.heartbeat</c>, <c>task.claimed</c>. Drives the SSE
/// <c>event:</c> field on the wire.
/// </param>
/// <param name="TenantId">
/// Owning tenant for the event. <c>null</c> for platform-wide events; only
/// surface-level subscribers with no tenant filter receive those.
/// </param>
/// <param name="Timestamp">UTC time the event was emitted by its source.</param>
/// <param name="Payload">
/// Arbitrary payload serialized as the SSE <c>data:</c> field. Must be
/// safe for <see cref="System.Text.Json.JsonSerializer.Serialize(object?, System.Text.Json.JsonSerializerOptions?)"/>.
/// </param>
public sealed record EngineLifecycleEvent(
    string Type,
    Guid? TenantId,
    DateTimeOffset Timestamp,
    object Payload);

/// <summary>
/// In-process pub/sub seam for engine / workflow / task-queue lifecycle
/// events. Consumed by the SSE endpoint at
/// <c>GET /api/engine/events/state</c> and <c>GET /api/engine/events/logs</c>
/// so dashboard <c>EventSource</c> clients see a continuous stream instead
/// of the single-frame shim the earlier port-gap remediation landed
/// (audit finding 012).
///
/// <para>Implementation note: the bus is deliberately process-local. For
/// multi-instance deployments the long-term plan is to front this with a
/// Redis pub/sub or a Postgres <c>LISTEN/NOTIFY</c> bridge once the
/// multi-engine registry (finding 013) is real. Until then, each API pod
/// only fans out events its own publishers emit.</para>
/// </summary>
public interface IEngineLifecycleBus
{
    /// <summary>
    /// Subscribe to events for a specific tenant. The returned async
    /// enumerable yields events in publication order until the
    /// <paramref name="ct"/> is cancelled (client disconnect) or the bus is
    /// disposed. Subscribers are non-blocking: if a subscriber falls behind
    /// its channel's bounded buffer, the oldest event is dropped and a
    /// gap-warning counter is incremented. Dashboards poll state on
    /// reconnect, so drop-oldest is preferable to back-pressuring
    /// publishers.
    /// </summary>
    /// <param name="tenantId">
    /// Filter: only events where
    /// <see cref="EngineLifecycleEvent.TenantId"/> matches this value are
    /// delivered. Platform-wide events (null TenantId on the published
    /// event) are broadcast to every subscriber regardless of tenant.
    /// </param>
    /// <param name="ct">Cancellation token tied to the HTTP request.</param>
    IAsyncEnumerable<EngineLifecycleEvent> SubscribeAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Publish an event to all matching subscribers. Non-blocking: the
    /// current implementation uses a bounded channel per subscriber with
    /// <c>DropOldest</c> back-pressure, so this method never blocks the
    /// publisher even if a subscriber is slow.
    /// </summary>
    ValueTask PublishAsync(EngineLifecycleEvent evt);

    /// <summary>
    /// Current number of active subscribers. Exposed for observability /
    /// tests; not used by endpoints.
    /// </summary>
    int SubscriberCount { get; }
}
