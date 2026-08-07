using Tamma.Api.Services.PlatformEvents;
using Tamma.Data.Entities;
using Tamma.Platforms;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Epic 31 P2 — the event-driven cache-invalidation SUBSCRIBER Story
/// 31-2 designed but never built. Subscribes the in-process
/// <see cref="IPlatformEventBus"/> for the three invalidation events
/// (<c>PLATFORM.INSTALLATION.CREDENTIAL_ROTATED</c>,
/// <c>PLATFORM.INSTALLATION.DISCONNECTED</c>,
/// <c>TENANT.SWITCH_ORG</c>) and evicts every cached driver for the
/// event's tenant via
/// <see cref="PlatformDriverCache.InvalidateTenantAsync"/> — so a
/// rotated / disconnected credential stops being used IMMEDIATELY
/// instead of only after the 5-minute TTL self-heal.
///
/// <para>Hosted-service shape: subscription happens at startup and is
/// disposed on shutdown. Handler failures are already swallowed by the
/// bus (a buggy subscriber never breaks publication); the TTL remains
/// the safety net for missed events (multi-pod deployments — the bus
/// is process-local).</para>
/// </summary>
public sealed class PlatformDriverCacheInvalidator : IHostedService
{
    /// <summary>Event types that evict (exact match).</summary>
    internal static readonly string[] InvalidatingEventTypes =
    [
        PlatformInstallationEventTypes.CredentialRotated,
        PlatformInstallationEventTypes.Disconnected,
        PlatformInstallationEventTypes.SwitchOrg,
    ];

    private readonly IPlatformEventBus _bus;
    private readonly PlatformDriverCache _cache;
    private readonly ILogger<PlatformDriverCacheInvalidator> _logger;
    private IDisposable? _subscription;

    public PlatformDriverCacheInvalidator(
        IPlatformEventBus bus,
        PlatformDriverCache cache,
        ILogger<PlatformDriverCacheInvalidator> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        _bus = bus;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(HandleAsync);
        _logger.LogInformation(
            "Platform driver-cache invalidator subscribed (events: {Events})",
            string.Join(", ", InvalidatingEventTypes));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    /// <summary>Bus handler — public-ish (internal) so the unit test
    /// can drive it without standing up a host.</summary>
    internal async Task HandleAsync(PlatformEvent evt, CancellationToken ct)
    {
        if (!InvalidatingEventTypes.Contains(evt.Type, StringComparer.Ordinal))
        {
            return;
        }
        if (evt.TenantId is not { } tenantId)
        {
            return;
        }

        await _cache.InvalidateTenantAsync(tenantId, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Evicted cached platform drivers for tenant {TenantId} on {EventType}",
            tenantId, evt.Type);
    }
}
