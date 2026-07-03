using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Streaming;

/// <summary>
/// Story 32-23 (AC5) — the default in-process <see cref="ILlmRunStreamBus"/>.
/// Singleton. Thread-safe. A <c>ConcurrentDictionary</c> of per-run topics,
/// each holding a monotonic seq counter and a set of bounded per-subscriber
/// channels (DropOldest, capacity <see cref="Capacity"/>).
///
/// <para><b>Decoupling guarantees (the reason the tap can never break the
/// engine):</b></para>
/// <list type="bullet">
///   <item>Publishing with no topic (no subscribers) is a TRUE no-op — no
///     allocation, no seq, nothing — so an un-watched run pays nothing.</item>
///   <item><see cref="PublishAsync"/> writes with <c>TryWrite</c> to
///     DropOldest channels: it never blocks and never throws into the
///     producer.</item>
///   <item>A <c>final</c> frame tears the topic down + completes every
///     subscriber channel, so live enumerations end cleanly and the topic's
///     seq counter + channel set don't linger.</item>
/// </list>
/// </summary>
public sealed class LlmRunStreamBus : ILlmRunStreamBus
{
    /// <summary>Bounded per-subscriber channel capacity. Mirrors the admin SSE
    /// endpoint's per-tick back-pressure discipline: a slow subscriber drops
    /// oldest frames rather than stalling the run.</summary>
    public const int Capacity = 256;

    private readonly ConcurrentDictionary<string, Topic> _topics =
        new(StringComparer.Ordinal);

    private readonly ILogger<LlmRunStreamBus>? _logger;

    public LlmRunStreamBus(ILogger<LlmRunStreamBus>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string correlationId, RunStreamFrame frame, CancellationToken ct = default)
    {
        // Fire-and-forget contract: NEVER throw into the producer. Any
        // unexpected failure is swallowed + logged; the buffered run always
        // proceeds (AC5 / AC6).
        try
        {
            if (string.IsNullOrEmpty(correlationId) || frame is null)
            {
                return ValueTask.CompletedTask;
            }

            // No topic => no subscribers => a TRUE no-op (an un-watched run is
            // never blocked, slowed, or failed by the absence of observers).
            if (!_topics.TryGetValue(correlationId, out var topic))
            {
                return ValueTask.CompletedTask;
            }

            var seq = Interlocked.Increment(ref topic.Seq);
            var stamped = frame with { Seq = seq };

            foreach (var kv in topic.Subscribers)
            {
                // DropOldest bounded channel: TryWrite is non-blocking and (short
                // of a completed channel) always accepts, dropping the oldest
                // queued frame under back-pressure. No producer stall, ever.
                if (!kv.Value.Writer.TryWrite(stamped))
                {
                    _logger?.LogWarning(
                        "run-stream subscriber channel refused a frame (completed/saturated); dropping. "
                        + "correlationId={CorrelationId} frameType={FrameType} seq={Seq}",
                        correlationId, frame.Type, seq);
                }
            }

            if (string.Equals(frame.Type, RunStreamFrameType.Final, StringComparison.Ordinal))
            {
                // The run is over: tear the topic down and complete every
                // subscriber channel so live-tail enumerations end cleanly and
                // the seq counter + channel set don't linger in memory.
                if (_topics.TryRemove(correlationId, out var removed))
                {
                    foreach (var kv in removed.Subscribers)
                    {
                        kv.Value.Writer.TryComplete();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // A publish must NEVER surface to the run. This branch is a bug guard
            // (the paths above don't throw) — logged at ERROR per the story's
            // logging requirements, never propagated.
            _logger?.LogError(ex,
                "run-stream publish threw despite the swallow guard; frame dropped, run unaffected. "
                + "correlationId={CorrelationId}", correlationId);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IRunStreamSubscription Subscribe(string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);

        var topic = _topics.GetOrAdd(correlationId, static _ => new Topic());

        var channel = Channel.CreateBounded<RunStreamFrame>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,   // the SSE handler races WaitToReadAsync vs a heartbeat timer
                SingleWriter = false,   // many producers (sink + ManagedAgent + inline runner)
            });

        var id = Guid.NewGuid();
        topic.Subscribers[id] = channel;

        _logger?.LogDebug(
            "run-stream subscriber attached. correlationId={CorrelationId} subscriberCount={Count}",
            correlationId, topic.Subscribers.Count);

        return new Subscription(this, correlationId, id, channel);
    }

    /// <inheritdoc />
    public int SubscriberCount(string correlationId)
        => _topics.TryGetValue(correlationId, out var topic) ? topic.Subscribers.Count : 0;

    private void Detach(string correlationId, Guid id)
    {
        if (_topics.TryGetValue(correlationId, out var topic)
            && topic.Subscribers.TryRemove(id, out var ch))
        {
            ch.Writer.TryComplete();
            // Intentionally DO NOT remove an empty topic here: keeping it until
            // the run's `final` frame preserves per-run seq monotonicity across a
            // subscriber that disconnects + reconnects mid-run. The topic is torn
            // down deterministically on `final` (or bounded by process lifetime
            // for a run that dies before emitting `final`).
            _logger?.LogDebug(
                "run-stream subscriber detached. correlationId={CorrelationId} subscriberCount={Count}",
                correlationId, topic.Subscribers.Count);
        }
    }

    /// <summary>Per-run fan-out state: a monotonic seq + the live subscriber
    /// channels keyed by an opaque id.</summary>
    private sealed class Topic
    {
        public long Seq;
        public readonly ConcurrentDictionary<Guid, Channel<RunStreamFrame>> Subscribers = new();
    }

    private sealed class Subscription : IRunStreamSubscription
    {
        private readonly LlmRunStreamBus _bus;
        private readonly string _correlationId;
        private readonly Guid _id;
        private readonly Channel<RunStreamFrame> _channel;
        private int _disposed;

        public Subscription(LlmRunStreamBus bus, string correlationId, Guid id, Channel<RunStreamFrame> channel)
        {
            _bus = bus;
            _correlationId = correlationId;
            _id = id;
            _channel = channel;
        }

        public ChannelReader<RunStreamFrame> Reader => _channel.Reader;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _bus.Detach(_correlationId, _id);
        }
    }
}
