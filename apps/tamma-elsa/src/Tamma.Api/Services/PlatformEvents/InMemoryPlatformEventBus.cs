using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.PlatformEvents;

/// <summary>
/// In-memory, process-local <see cref="IPlatformEventBus"/>. Holds a
/// concurrent list of subscriptions and dispatches each published event
/// sequentially to every matching handler.
///
/// <para>Lifetime: singleton. Subscribers register at composition root
/// (Program.cs) and remain registered until the returned
/// <see cref="IDisposable"/> is disposed.</para>
///
/// <para>Per the bus contract, handler exceptions are caught and
/// logged at <see cref="LogLevel.Error"/> — they never propagate to the
/// publisher. A misbehaving subscriber must not abort the workflow that
/// emitted the event.</para>
/// </summary>
public sealed class InMemoryPlatformEventBus : IPlatformEventBus
{
    private sealed record Subscription(
        Guid Id,
        string? TypePrefix,
        PlatformEventHandler Handler);

    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly ILogger<InMemoryPlatformEventBus> _logger;

    public InMemoryPlatformEventBus(ILogger<InMemoryPlatformEventBus> logger)
    {
        _logger = logger;
    }

    public int SubscriberCount => _subscriptions.Count;

    public IDisposable Subscribe(PlatformEventHandler handler)
        => Subscribe(typePrefix: null!, handler);

    public IDisposable Subscribe(string typePrefix, PlatformEventHandler handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var sub = new Subscription(Guid.NewGuid(), typePrefix, handler);
        _subscriptions[sub.Id] = sub;
        return new SubscriptionToken(_subscriptions, sub.Id);
    }

    public async Task PublishAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        // Snapshot subscriptions to insulate dispatch from concurrent
        // (un)subscription.
        var snapshot = _subscriptions.Values.ToArray();

        foreach (var sub in snapshot)
        {
            if (ct.IsCancellationRequested) return;

            if (!Matches(sub.TypePrefix, evt.Type)) continue;

            try
            {
                await sub.Handler(evt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // NEVER rethrow — the publisher must not learn that a
                // subscriber misbehaved. Log + continue with the next
                // subscriber.
                _logger.LogError(ex,
                    "Platform event subscriber {SubscriberId} threw on event " +
                    "{EventType} (id={EventId}); other subscribers continue",
                    sub.Id, evt.Type, evt.Id);
            }
        }
    }

    public async Task<PlatformEvent?> AppendAndPublishAsync(
        IPlatformEventRepository repository,
        PlatformEvent evt,
        CancellationToken ct = default)
    {
        if (repository is null) throw new ArgumentNullException(nameof(repository));
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        var persisted = await repository.AppendAsync(evt, ct).ConfigureAwait(false);
        if (persisted is null)
        {
            // Dedup no-op — the original event was already published when
            // the first append happened. Skipping a re-publish here is
            // the whole point of the partial-unique step-dedup index.
            return null;
        }

        await PublishAsync(persisted, ct).ConfigureAwait(false);
        return persisted;
    }

    private static bool Matches(string? typePrefix, string eventType)
    {
        if (string.IsNullOrEmpty(typePrefix)) return true;
        return eventType.StartsWith(typePrefix, StringComparison.Ordinal);
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, Subscription> _subs;
        private readonly Guid _id;
        private int _disposed;

        public SubscriptionToken(
            ConcurrentDictionary<Guid, Subscription> subs, Guid id)
        {
            _subs = subs;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _subs.TryRemove(_id, out _);
        }
    }
}
