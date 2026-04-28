using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Api.Services.Analytics;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services;

/// <summary>
/// Options controlling <see cref="PoolWarmupService"/>. Bound to the
/// <c>TenantConnectionPool:Warmup</c> configuration sub-section.
/// </summary>
public sealed class PoolWarmupOptions
{
    /// <summary>Configuration section name for binding.</summary>
    public const string SectionName = "TenantConnectionPool:Warmup";

    /// <summary>
    /// Master kill-switch. Default <c>false</c> so deployments without
    /// the analytics rollup populated (Story 28-10 incomplete or fresh
    /// install) don't pay startup cost on a cold cache. Operators flip
    /// this on once the analytics table has data.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Number of top-active tenants to pre-warm. Default 10 — small
    /// enough that startup cost stays under a few seconds even with
    /// cold-tier Postgres latency.
    /// </summary>
    public int TopTenants { get; set; } = 10;

    /// <summary>
    /// Per-tenant warmup timeout in seconds. Default 5 — a tenant whose
    /// pool fails to build in 5s is logged + skipped; the others still
    /// warm.
    /// </summary>
    public int PerTenantTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Story 28-4 AC (warmup) — optional <see cref="BackgroundService"/>
/// that pre-warms the per-tenant connection pool for the top-N
/// most-recently-active tenants on process boot. The first request from
/// each warmed tenant skips the cold-miss build path (CP lookup +
/// decrypt + Npgsql data-source build), which can cut p95 cold-start
/// latency from ~50ms down to a single cache-hit lookup.
///
/// <para><b>Dependency on Story 28-10</b>: the warmup target list comes
/// from <see cref="IPlatformAnalyticsService.GetTopTenantsAsync"/>
/// (analytics rollup). On a fresh install (no analytics rows yet) the
/// service logs <c>pool.warmup.empty</c> and exits cleanly — no
/// crash. The default <see cref="PoolWarmupOptions.Enabled"/> is
/// <c>false</c> so even the analytics call is skipped until an
/// operator flips it on.</para>
///
/// <para><b>Failure isolation</b>: a single tenant's cold-miss failure
/// (e.g. its CP row is in <c>provisioning</c> state) is logged at WARN
/// and skipped — the warmup loop continues. Startup never blocks on a
/// per-tenant build; each pool gets <see cref="PoolWarmupOptions.PerTenantTimeoutSeconds"/>
/// before the warmup moves on.</para>
///
/// <para><b>Lifetime (round-2 M11)</b>: extends
/// <see cref="BackgroundService"/> rather than implementing
/// <see cref="IHostedService"/> directly. The host runtime starts
/// <see cref="ExecuteAsync"/> on its own background task and threads
/// the <c>stoppingToken</c> through to the warmup loop, so a
/// shutdown mid-warmup cancels the loop cleanly and
/// <c>BackgroundService.StopAsync</c> awaits the warmup task before
/// the host disposes scopes the warmup is still using.</para>
/// </summary>
public sealed class PoolWarmupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<PoolWarmupOptions> _options;
    private readonly ILogger<PoolWarmupService> _logger;

    public PoolWarmupService(
        IServiceProvider services,
        IOptions<PoolWarmupOptions> options,
        ILogger<PoolWarmupService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The host runtime invokes <c>ExecuteAsync</c> with a stopping
    /// token tied to host shutdown. The warmup runs on this task; if
    /// the host shuts down mid-warmup the token cancels and the loop
    /// exits gracefully. <see cref="BackgroundService.StopAsync"/>
    /// awaits this task during shutdown so scopes outlive the warmup.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "pool.warmup.disabled — set TenantConnectionPool:Warmup:Enabled=true to opt in.");
            return;
        }

        await WarmupAsync(opts, stoppingToken).ConfigureAwait(false);
    }

    private async Task WarmupAsync(
        PoolWarmupOptions opts,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var analytics = scope.ServiceProvider
                .GetService<IPlatformAnalyticsService>();
            var resolver = scope.ServiceProvider
                .GetService<ITenantConnectionResolver>();

            if (analytics is null || resolver is null)
            {
                _logger.LogWarning(
                    "pool.warmup.skipped — IPlatformAnalyticsService or " +
                    "ITenantConnectionResolver not registered.");
                return;
            }

            var top = await analytics.GetTopTenantsAsync(
                opts.TopTenants, stoppingToken).ConfigureAwait(false);
            if (top.Count == 0)
            {
                _logger.LogInformation(
                    "pool.warmup.empty — analytics returned no active " +
                    "tenants. Nothing to warm.");
                return;
            }

            var warmed = 0;
            var failed = 0;
            foreach (var tenant in top)
            {
                if (stoppingToken.IsCancellationRequested) break;

                using var perTenantCts = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken);
                perTenantCts.CancelAfter(
                    TimeSpan.FromSeconds(Math.Max(1, opts.PerTenantTimeoutSeconds)));

                try
                {
                    _ = await resolver.GetDataSourceAsync(
                        tenant.TenantId, perTenantCts.Token).ConfigureAwait(false);
                    warmed++;
                }
                catch (OperationCanceledException) when (perTenantCts.IsCancellationRequested
                                                        && !stoppingToken.IsCancellationRequested)
                {
                    failed++;
                    _logger.LogWarning(
                        "pool.warmup.timeout tenantId={TenantId}",
                        tenant.TenantId);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(
                        ex,
                        "pool.warmup.failed tenantId={TenantId}",
                        tenant.TenantId);
                }
            }

            _logger.LogInformation(
                "pool.warmup.complete warmed={Warmed} failed={Failed} " +
                "from top {Total} tenants",
                warmed,
                failed,
                top.Count);
        }
        catch (Exception ex)
        {
            // Never let warmup take down the host.
            _logger.LogError(
                ex,
                "pool.warmup.crashed — service is not running but the " +
                "API is still serving cold-path requests.");
        }
    }
}
