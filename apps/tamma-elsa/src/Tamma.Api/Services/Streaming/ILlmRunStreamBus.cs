using System.Threading.Channels;

namespace Tamma.Api.Services.Streaming;

/// <summary>
/// Story 32-23 (AC5) — the in-process pub/sub that decouples a live human tap
/// from the engine's buffered <c>/llm/call</c>. Keyed by <c>correlationId</c>
/// (== the workflow instance id threaded through the tool-loop emitter and the
/// <c>LlmCallRequest</c>). Single-instance only — mirrors
/// <c>WebhookSignalRegistry</c>'s in-process registry discipline; cross-process
/// fan-out (Redis / Postgres LISTEN-NOTIFY) is a deferred open decision.
///
/// <para><b>Load-bearing invariant:</b> the producer side (ManagedAgent /
/// runner / sink) treats the bus as fire-and-forget. Publishing with ZERO
/// subscribers is a no-op; <see cref="PublishAsync"/> NEVER throws into the
/// producer and NEVER blocks it — so the buffered run is never slowed, failed,
/// or retried because of an observer's absence or slowness (AC5 / AC6).</para>
/// </summary>
public interface ILlmRunStreamBus
{
    /// <summary>
    /// Publish one frame for <paramref name="correlationId"/>. Fire-and-forget:
    /// a no-op when there are no subscribers, non-blocking, and it never throws
    /// into the caller. The bus stamps the frame's per-run monotonic
    /// <see cref="RunStreamFrame.Seq"/>. When the frame is
    /// <see cref="RunStreamFrameType.Final"/> the run's topic is torn down and
    /// every subscriber channel is completed (clean live-tail termination).
    /// </summary>
    ValueTask PublishAsync(string correlationId, RunStreamFrame frame, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to the live tail for <paramref name="correlationId"/>. Returns
    /// a disposable subscription over a bounded per-subscriber channel
    /// (capacity ~256, DropOldest back-pressure — a slow/abandoned subscriber
    /// drops oldest frames rather than stalling the producer). Dispose to
    /// detach (removes the channel so nothing leaks on disconnect).
    /// </summary>
    IRunStreamSubscription Subscribe(string correlationId);

    /// <summary>Current subscriber count for a run — diagnostics / tests.</summary>
    int SubscriberCount(string correlationId);
}

/// <summary>
/// A live-tail subscription handle. Dispose to detach the underlying channel
/// from the bus (so an SSE handler that returns / a disconnected client never
/// leaks its subscription).
/// </summary>
public interface IRunStreamSubscription : IDisposable
{
    /// <summary>The bounded reader the SSE handler pumps. Drained
    /// single-reader; completes when the run publishes its <c>final</c> frame
    /// or the subscription is disposed.</summary>
    ChannelReader<RunStreamFrame> Reader { get; }
}
