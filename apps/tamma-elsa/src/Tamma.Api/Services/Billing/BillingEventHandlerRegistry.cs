namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — resolves an <see cref="IBillingEventHandler"/> for a Stripe
/// event type. Mirrors <c>PlatformTaskHandlerRegistry</c>: a per-scope snapshot
/// dictionary keyed by event type, with duplicate-claim detection at
/// construction. An unclaimed type returns <c>null</c> (the processor then uses
/// <see cref="NullBillingEventHandler"/>).
/// </summary>
public interface IBillingEventHandlerRegistry
{
    /// <summary>Resolve a handler for <paramref name="eventType"/>, or <c>null</c>.</summary>
    IBillingEventHandler? Resolve(string eventType);

    /// <summary>All claimed Stripe event types (admin diagnostics).</summary>
    IReadOnlyCollection<string> RegisteredEventTypes { get; }
}

/// <summary>
/// Default <see cref="IBillingEventHandlerRegistry"/> backed by a snapshot
/// dictionary built per scope from every registered
/// <see cref="IBillingEventHandler"/>. Two handlers claiming the same event type
/// throw at construction so a misconfiguration is caught on the first claim
/// (mirrors <c>PlatformTaskHandlerRegistry</c>).
///
/// <para><see cref="NullBillingEventHandler"/> is excluded from the registry —
/// it is the explicit fallthrough the processor applies when
/// <see cref="Resolve"/> is null, never a registered claimant.</para>
/// </summary>
public sealed class BillingEventHandlerRegistry : IBillingEventHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IBillingEventHandler> _byType;

    public BillingEventHandlerRegistry(IEnumerable<IBillingEventHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var dict = new Dictionary<string, IBillingEventHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (handler is NullBillingEventHandler) continue;
            foreach (var type in handler.HandledEventTypes)
            {
                if (string.IsNullOrWhiteSpace(type))
                    throw new InvalidOperationException(
                        $"IBillingEventHandler '{handler.GetType().FullName}' "
                        + "claimed an empty event type.");
                if (dict.TryGetValue(type, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate IBillingEventHandler registration for Stripe "
                        + $"event type '{type}': {existing.GetType().FullName} vs "
                        + $"{handler.GetType().FullName}.");
                dict[type] = handler;
            }
        }
        _byType = dict;
    }

    public IBillingEventHandler? Resolve(string eventType) =>
        _byType.TryGetValue(eventType ?? string.Empty, out var h) ? h : null;

    public IReadOnlyCollection<string> RegisteredEventTypes => _byType.Keys.ToArray();
}
