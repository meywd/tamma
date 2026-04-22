using Microsoft.Extensions.Hosting;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Background <see cref="IHostedService"/> that periodically evicts idle
/// provider sessions, matching the TS engine's <c>setInterval</c>-based
/// cleanup pass (Story 9-4).
/// </summary>
/// <remarks>
/// <para>
/// Runs every <see cref="ProviderSessionOptions.CleanupInterval"/> (default
/// 60 seconds) and evicts sessions idle for more than
/// <see cref="ProviderSessionOptions.InactivityTtl"/> (default 30 minutes).
/// </para>
/// <para>
/// The loop catches and logs any exception rather than crashing the host —
/// an eviction pass that fails must not take the API down.
/// </para>
/// </remarks>
public sealed class ProviderSessionCleanupService : BackgroundService
{
    private readonly IProviderSessionService _sessions;
    private readonly ProviderSessionOptions _options;
    private readonly ILogger<ProviderSessionCleanupService> _logger;

    public ProviderSessionCleanupService(
        IProviderSessionService sessions,
        ProviderSessionOptions options,
        ILogger<ProviderSessionCleanupService> logger)
    {
        _sessions = sessions;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ProviderSessionCleanupService started (interval={Interval}, ttl={Ttl})",
            _options.CleanupInterval, _options.InactivityTtl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var evicted = await _sessions.EvictInactiveAsync(_options.InactivityTtl);
                if (evicted > 0)
                {
                    _logger.LogInformation("Evicted {Count} idle provider sessions", evicted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ProviderSessionCleanupService eviction pass failed");
            }

            try
            {
                await Task.Delay(_options.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
