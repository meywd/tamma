using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformEvents;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-6 — evicts cached entitlement snapshots in reaction to plan /
/// catalog changes. Subscribes the in-process <see cref="IPlatformEventBus"/>
/// (the same bus <c>TenantStatusInvalidationListener</c> /
/// <c>NotificationDispatcher</c> use) — a near-clone of that listener but
/// SIMPLER: no Postgres LISTEN/NOTIFY, no connection management, no shutdown
/// drain (the bus is in-process).
///
/// <list type="bullet">
///   <item><description><c>TENANT.PLAN.CHANGED</c> (34-4) for tenant T ⇒
///     evict exactly T's snapshot.</description></item>
///   <item><description>A catalog edit (<c>PLAN.VERSION.CREATED</c> /
///     <c>PLAN.DEPRECATED</c>) ⇒ flush the whole cache (cheap; pinned snapshots
///     re-read correctly on the next miss).</description></item>
/// </list>
///
/// <para>Handlers are best-effort and never throw back into the bus (the bus
/// already catches, but a malformed event must evict NOTHING rather than flush
/// everything — see <see cref="OnTenantPlanChangedAsync"/>).</para>
/// </summary>
public sealed class EntitlementCacheInvalidationListener : BackgroundService
{
    private readonly IPlatformEventBus _bus;
    private readonly IEntitlementSnapshotCache _cache;
    private readonly ILogger<EntitlementCacheInvalidationListener> _logger;

    private IDisposable? _tenantPlanSub;
    private IDisposable? _catalogSub;

    public EntitlementCacheInvalidationListener(
        IPlatformEventBus bus,
        IEntitlementSnapshotCache cache,
        ILogger<EntitlementCacheInvalidationListener> logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe once at startup; the returned tokens are disposed on stop.
        _tenantPlanSub = _bus.Subscribe(
            EntitlementEventTypes.TenantPlanChangedPrefix, OnTenantPlanChangedAsync);
        _catalogSub = _bus.Subscribe(
            EntitlementEventTypes.PlanCatalogPrefix, OnCatalogChangedAsync);

        _logger.LogInformation(
            "EntitlementCacheInvalidationListener subscribed ({TenantPrefix} → evict, {CatalogPrefix} → flush)",
            EntitlementEventTypes.TenantPlanChangedPrefix,
            EntitlementEventTypes.PlanCatalogPrefix);

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _tenantPlanSub?.Dispose();
        _catalogSub?.Dispose();
        _tenantPlanSub = null;
        _catalogSub = null;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Per-tenant eviction on a <c>TENANT.PLAN.*</c> event. Reads the tenant id
    /// from the event's <c>TenantId</c> column, falling back to the
    /// <c>tenantId</c> tag. A malformed event (no resolvable tenant) evicts
    /// NOTHING — never a blanket flush.
    /// </summary>
    private Task OnTenantPlanChangedAsync(PlatformEvent evt, CancellationToken ct)
    {
        try
        {
            var tenantId = ResolveTenantId(evt);
            if (tenantId is null)
            {
                _logger.LogWarning(
                    "Entitlement invalidation: {Type} carried no resolvable tenantId; evicting nothing",
                    evt.Type);
                return Task.CompletedTask;
            }

            _cache.Invalidate(tenantId.Value);
            _logger.LogDebug(
                "Entitlement invalidation: evicted tenant {TenantId} snapshot on {Type}",
                tenantId, evt.Type);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders — the bus already swallows, but keep the
            // cache untouched for unrelated tenants on a handler fault.
            _logger.LogWarning(
                ex, "Entitlement invalidation handler threw on {Type}; ignored", evt.Type);
        }

        return Task.CompletedTask;
    }

    /// <summary>Full flush on any catalog edit (<c>PLAN.*</c>).</summary>
    private Task OnCatalogChangedAsync(PlatformEvent evt, CancellationToken ct)
    {
        try
        {
            _cache.Flush();
            _logger.LogInformation(
                "Entitlement invalidation: flushed all snapshots on catalog event {Type}", evt.Type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Entitlement invalidation flush threw on {Type}; ignored", evt.Type);
        }

        return Task.CompletedTask;
    }

    private static Guid? ResolveTenantId(PlatformEvent evt)
    {
        if (evt.TenantId is Guid fromColumn && fromColumn != Guid.Empty)
        {
            return fromColumn;
        }

        if (string.IsNullOrWhiteSpace(evt.Tags))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(evt.Tags);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tenantId", out var el)
                && el.ValueKind == JsonValueKind.String
                && Guid.TryParse(el.GetString(), out var fromTag))
            {
                return fromTag;
            }
        }
        catch (JsonException)
        {
            // Malformed tags → no tenant → evict nothing.
        }

        return null;
    }
}
