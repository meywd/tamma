using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Tamma.Api.Services.Engine.Lifecycle;

/// <summary>
/// In-memory, process-local <see cref="IEngineLifecycleBus"/>.
///
/// <para>Each call to <see cref="SubscribeAsync"/> allocates a bounded
/// <see cref="Channel{T}"/> keyed by a subscription GUID under the
/// subscriber's tenant. Publishers fan out to the tenant's subscribers plus
/// any tenant-agnostic (platform-wide) subscribers, using
/// <see cref="BoundedChannelFullMode.DropOldest"/> so a slow subscriber
/// never stalls a publisher.</para>
///
/// <para>Lifetime: singleton. Subscriptions are torn down when the
/// subscriber's cancellation token fires; the bus itself lives for the
/// process lifetime.</para>
///
/// <para>Limitations (see audit finding 012): events are visible only to
/// the process that published them. Multi-pod deployments need a real
/// pub/sub bridge — Redis, or Postgres <c>LISTEN/NOTIFY</c> bound to
/// <c>domain_events</c> inserts — before the dashboard can aggregate
/// state across pods.</para>
/// </summary>
public sealed class InMemoryEngineLifecycleBus : IEngineLifecycleBus, IDisposable
{
    private sealed record Subscription(
        Guid Id,
        Guid? TenantId,
        Channel<EngineLifecycleEvent> Channel);

    // Bounded so a stuck client cannot exhaust memory. The dashboard polls
    // state on reconnect, so 256 is an ample ceiling for short bursts.
    private const int ChannelCapacity = 256;

    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private int _disposed;

    public int SubscriberCount => _subscriptions.Count;

    public async IAsyncEnumerable<EngineLifecycleEvent> SubscribeAsync(
        Guid tenantId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(InMemoryEngineLifecycleBus));

        var channel = Channel.CreateBounded<EngineLifecycleEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var subscription = new Subscription(Guid.NewGuid(), tenantId, channel);
        _subscriptions[subscription.Id] = subscription;

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _subscriptions.TryRemove(subscription.Id, out _);
            channel.Writer.TryComplete();
        }
    }

    public ValueTask PublishAsync(EngineLifecycleEvent evt)
    {
        if (evt is null) throw new ArgumentNullException(nameof(evt));
        if (Volatile.Read(ref _disposed) == 1) return ValueTask.CompletedTask;

        // Fan out: deliver to subscribers whose tenant matches the event's
        // tenant, OR when the event has no tenant (platform-wide). A
        // published event with a specific TenantId does NOT leak across
        // tenants — this is the finding-016 tenant-scoping contract.
        foreach (var sub in _subscriptions.Values)
        {
            var matches = evt.TenantId is null || evt.TenantId == sub.TenantId;
            if (!matches) continue;

            // TryWrite never blocks; DropOldest handles overflow.
            sub.Channel.Writer.TryWrite(evt);
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        foreach (var sub in _subscriptions.Values)
        {
            sub.Channel.Writer.TryComplete();
        }
        _subscriptions.Clear();
    }
}
