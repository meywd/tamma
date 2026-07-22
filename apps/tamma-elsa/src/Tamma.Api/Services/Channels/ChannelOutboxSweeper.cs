using Microsoft.Extensions.Options;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Channels;

/// <summary>
/// Options for <see cref="ChannelOutboxSweeper"/>. Mirrors <c>OutboxSmtpSenderOptions</c>'
/// poll-cadence + <c>RunOnStartup</c> test gate.
/// </summary>
public sealed class ChannelOutboxSweeperOptions
{
    /// <summary>How often the sweeper walks tenants with stale rows. Default 15s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A <c>delivered</c> row whose <c>DeliveredAt</c> is older than this is treated as
    /// stale (a missed reconnect race) and re-published. <c>pending</c> rows (never
    /// delivered — crash between persist and publish) are always stale. Default 60s.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Max rows re-published per tenant per pass (bounds one cycle's work).</summary>
    public int BatchPerTenant { get; set; } = 200;

    /// <summary>
    /// When <c>false</c> the sweeper does not start its poll loop — the shared test
    /// fixture gate so outbox-row-state assertions don't race the loop. Mirrors
    /// <c>OutboxSmtpSenderOptions.RunOnStartup</c>.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>
/// Story 39-18 (D6) — the slow sweeper. Re-publishes stale <c>pending</c>/<c>delivered</c>
/// -but-unacked channel rows across tenants, covering crash-between-persist-and-publish
/// and missed reconnect races (a SignalR group send to zero subscribers is a silent
/// no-op — the outbox is why single-instance is safe, just slower on failover). No
/// timeout here ever converts an unanswered request into a decision (AC7). Copies
/// <c>OutboxSmtpSender</c>'s options/<c>RunOnStartup</c> shape.
/// </summary>
public sealed class ChannelOutboxSweeper : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ChannelOutboxSweeperOptions _options;
    private readonly ILogger<ChannelOutboxSweeper> _logger;

    public ChannelOutboxSweeper(
        IServiceProvider serviceProvider,
        ChannelOutboxSweeperOptions options,
        ILogger<ChannelOutboxSweeper> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug("ChannelOutboxSweeper gated off (RunOnStartup=false); skipping poll loop.");
            return;
        }

        _logger.LogInformation("ChannelOutboxSweeper started. Poll interval={Interval}", _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChannelOutboxSweeper cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ChannelOutboxSweeper stopped");
    }

    /// <summary>
    /// One sweep pass: for each tenant with unacked rows, re-publish the stale ones.
    /// Exposed for tests so they don't race the polling timer.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IChannelOutboxRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ChannelOutboxService>();

        var staleBefore = DateTime.UtcNow - _options.StaleAfter;
        var republished = 0;

        var tenants = await outbox.ListTenantsWithPendingAsync(ct);
        foreach (var tenantId in tenants)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var stale = await outbox.ListStaleAsync(tenantId, staleBefore, _options.BatchPerTenant, ct);
                foreach (var row in stale)
                {
                    await service.RepublishAsync(row, ct);
                    republished++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's transient failure must not starve the rest of the pass.
                _logger.LogWarning(ex, "ChannelOutboxSweeper: tenant {TenantId} sweep failed", tenantId);
            }
        }

        return republished;
    }
}
