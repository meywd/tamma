using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Engine.Lifecycle;

/// <summary>
/// Periodically snapshots <see cref="IEngineRegistry"/> and publishes the
/// per-tenant engine state to the <see cref="IEngineLifecycleBus"/> as
/// <c>engine.heartbeat</c> events. Dashboards subscribed to the SSE
/// lifecycle stream get a reliable ~30s cadence of engine state even when
/// the engine has not had a concrete lifecycle transition.
///
/// <para>Finding 012 — this is the fallback signal source. Real
/// <c>engine.registered</c> / <c>engine.deregistered</c> events need a
/// real <c>TammaEngine</c> abstraction (finding 013); the registry today
/// only materialises synthetic entries from the workflow store. Until
/// that ports, this heartbeat service is the only thing the registry
/// contributes to the lifecycle stream.</para>
/// </summary>
public sealed class EngineRegistryHeartbeatService : BackgroundService
{
    /// <summary>Publish cadence. 30s is a compromise between staleness and bus chatter.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly IEngineLifecycleBus _bus;
    private readonly ILogger<EngineRegistryHeartbeatService> _logger;

    public EngineRegistryHeartbeatService(
        IServiceProvider services,
        IEngineLifecycleBus bus,
        ILogger<EngineRegistryHeartbeatService> logger)
    {
        _services = services;
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EngineRegistryHeartbeatService started (cadence={Cadence})", Cadence);
        using var timer = new PeriodicTimer(Cadence);
        try
        {
            // Emit once immediately so a freshly-started dashboard sees a
            // heartbeat without waiting a full cadence.
            await PublishOnceAsync(stoppingToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PublishOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EngineRegistryHeartbeatService terminated unexpectedly");
        }
    }

    /// <summary>Exposed for tests — one cycle, no timer.</summary>
    public async Task PublishOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IEngineRegistry>();
            var engines = await registry.ListAsync(tenantId: null, ct).ConfigureAwait(false);

            if (engines.Count == 0)
            {
                // Emit a tenant-agnostic heartbeat so every subscriber knows
                // the bus is alive even when the registry is empty.
                await _bus.PublishAsync(new EngineLifecycleEvent(
                    Type: "engine.heartbeat",
                    TenantId: null,
                    Timestamp: DateTimeOffset.UtcNow,
                    Payload: new { engineCount = 0 })).ConfigureAwait(false);
                return;
            }

            foreach (var engine in engines)
            {
                await _bus.PublishAsync(new EngineLifecycleEvent(
                    Type: "engine.heartbeat",
                    TenantId: engine.TenantId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Payload: new
                    {
                        id = engine.Id,
                        state = engine.State,
                        totalEvents = engine.Stats.TotalEvents,
                        lastEventAt = engine.Stats.LastEventAt
                    })).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never let a single bad cycle take down the hosted service.
            _logger.LogWarning(ex, "EngineRegistryHeartbeatService publish cycle failed");
        }
    }
}
