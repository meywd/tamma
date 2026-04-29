using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Webhooks;

/// <summary>
/// Story 31-7 AC5 / AC6 — in-process webhook handler dispatcher.
///
/// <para><b>Single-handler-per-event invariant</b>: the registry is keyed
/// by <c>(<see cref="PlatformKind"/>, eventTypePattern)</c>. A second
/// registration under the same key throws — multi-handler dispatch is
/// deferred to a follow-up story.</para>
///
/// <para><b>Pattern matching</b>: when dispatching an event the
/// dispatcher computes the candidate keys from the event:
/// <list type="number">
///   <item>If <see cref="PlatformWebhookEvent.Action"/> is non-null:
///         exact <c>{eventType}.{action}</c>, then wildcard
///         <c>{eventType}.*</c>, then bare <c>{eventType}</c>.</item>
///   <item>If <see cref="PlatformWebhookEvent.Action"/> is null: bare
///         <c>{eventType}</c> only.</item>
/// </list>
/// First match wins (most-specific first). Patterns are case-sensitive.</para>
///
/// <para><b>Cross-tenant safety</b>: the dispatcher only inspects
/// <see cref="PlatformWebhookEvent.Kind"/> + the pattern table — it does
/// NOT widen the event's tenant scope. A handler registered for
/// <c>PlatformKind.GitHub</c> will never see a Gitea event regardless
/// of pattern overlap.</para>
///
/// <para><b>Failure isolation</b>: a thrown exception is caught,
/// logged, and reported to the optional <see cref="HandlerFailedHook"/>
/// (the receiver wires this up to emit a
/// <c>PLATFORM.WEBHOOK.HANDLER_FAILED</c> control-plane event). The
/// dispatcher never re-throws.</para>
/// </summary>
public sealed class WebhookEventDispatcher : IWebhookEventDispatcher
{
    private readonly ConcurrentDictionary<HandlerKey, IWebhookHandler> _handlers = new();
    private readonly ILogger<WebhookEventDispatcher> _logger;

    /// <summary>
    /// Optional hook invoked when a handler throws. The receiver wires
    /// this up at startup to emit a control-plane
    /// <c>PLATFORM.WEBHOOK.HANDLER_FAILED</c> event. Null in tests
    /// where the failure is asserted directly.
    /// </summary>
    public Func<PlatformWebhookEvent, IWebhookHandler, Exception, CancellationToken, Task>? HandlerFailedHook { get; set; }

    public WebhookEventDispatcher(ILogger<WebhookEventDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public int HandlerCount => _handlers.Count;

    /// <inheritdoc />
    public void RegisterHandler(IWebhookHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(handler.EventTypePattern);

        var key = new HandlerKey(handler.Kind, handler.EventTypePattern);
        if (!_handlers.TryAdd(key, handler))
        {
            throw new InvalidOperationException(
                $"A webhook handler is already registered for ({handler.Kind}, '{handler.EventTypePattern}'). " +
                "Single-handler-per-event invariant: multi-handler dispatch is a future story.");
        }
    }

    /// <inheritdoc />
    public async Task<int> DispatchAsync(
        PlatformWebhookEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var handler = ResolveHandler(evt);
        if (handler is null)
        {
            _logger.LogDebug(
                "No handler registered for {Kind}/{EventType}/{Action}",
                evt.Kind, evt.EventType, evt.Action ?? "<no-action>");
            return 0;
        }

        // Defence in depth — keyed-DI mis-registration scenario where a
        // GitHub handler ends up under a Gitea key. Refuse to dispatch
        // cross-platform.
        if (handler.Kind != evt.Kind)
        {
            _logger.LogError(
                "Handler {HandlerType} registered under {RegisteredKind} but dispatched event for {EventKind}; refusing",
                handler.GetType().Name, handler.Kind, evt.Kind);
            return 0;
        }

        try
        {
            await handler.HandleAsync(evt, ct).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cooperative cancellation — propagate so the receiver can
            // fail the request cleanly. Only re-throw on caller-side
            // cancellation; uncooperative cancellations from the
            // handler are caught below.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Webhook handler {HandlerType} ({Kind}/{Pattern}) threw on {EventType}/{Action}",
                handler.GetType().Name, handler.Kind, handler.EventTypePattern,
                evt.EventType, evt.Action ?? "<no-action>");

            if (HandlerFailedHook is not null)
            {
                try
                {
                    await HandlerFailedHook(evt, handler, ex, ct).ConfigureAwait(false);
                }
                catch (Exception hookEx)
                {
                    _logger.LogError(hookEx,
                        "HandlerFailedHook itself threw — swallowing to keep dispatcher stable");
                }
            }
            return 1; // we did invoke the handler, even though it failed
        }
    }

    private IWebhookHandler? ResolveHandler(PlatformWebhookEvent evt)
    {
        // Most-specific first.
        if (!string.IsNullOrEmpty(evt.Action))
        {
            var exact = $"{evt.EventType}.{evt.Action}";
            if (_handlers.TryGetValue(new HandlerKey(evt.Kind, exact), out var h))
            {
                return h;
            }
            var wildcard = $"{evt.EventType}.*";
            if (_handlers.TryGetValue(new HandlerKey(evt.Kind, wildcard), out h))
            {
                return h;
            }
        }
        if (_handlers.TryGetValue(new HandlerKey(evt.Kind, evt.EventType), out var bare))
        {
            return bare;
        }
        return null;
    }

    private readonly record struct HandlerKey(PlatformKind Kind, string Pattern);
}
