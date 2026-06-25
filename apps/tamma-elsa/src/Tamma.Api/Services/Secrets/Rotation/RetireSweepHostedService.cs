using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Options for <see cref="RetireSweepHostedService"/>. Bound to the
/// <c>RetireSweep</c> configuration section.
/// </summary>
public sealed class RetireSweepOptions
{
    public const string SectionName = "RetireSweep";

    /// <summary>
    /// How often the sweeper drains due <c>RETIRE_SECRET_VERSION</c> rows.
    /// Default 1 minute — retires are low-frequency (grace windows are in
    /// minutes-to-hours) so a coarse cadence keeps the queue scan cheap
    /// while still draining a due retire promptly after its window opens.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Story 29-6 (review fix) — the ACTIVE retire-tail drainer.
///
/// <para><b>Why this exists.</b> The retire tail must drain even though
/// <c>PlatformTaskWorker:RunOnStartup</c> stays <c>false</c> (the worker is
/// not yet safe to enable for ALL platform-task types). The AC8-specified
/// per-task <see cref="RetireSecretVersionTaskHandler"/> only runs WHEN that
/// worker polls; with the worker gated off, nothing was draining
/// <c>RETIRE_SECRET_VERSION</c> rows at all. This dedicated hosted service
/// periodically calls <see cref="RetireScheduler.SweepDueRetireTasksAsync"/>
/// — which reserves ONLY due rows (the <c>VisibleAt</c> reservation guard
/// keeps not-yet-due rows untouched and never dead-letters them) and routes
/// each through the SAME <see cref="IRetireTaskExecutor"/> body the handler
/// uses, so the two drainers can't drift.</para>
///
/// <para><b>Independent of <c>PlatformTaskWorker</c>.</b> The sweeper's
/// <c>ReserveNextAsync</c> only acts on <c>RETIRE_SECRET_VERSION</c> rows it
/// can parse; any other reserved type is immediately put back
/// (<c>FailAsync(maxRetries: int.MaxValue)</c>) so it stays pending for the
/// (currently gated-off) generic worker. Running this sweeper does NOT
/// enable the generic worker and does NOT change any other task type's
/// reservation or dead-letter behaviour.</para>
///
/// <para>Mirrors the <see cref="SecretAutoRotationScheduler"/> /
/// <c>HourlyAnalyticsRollupScheduler</c> shape: a lightweight
/// poll-the-clock <see cref="BackgroundService"/>. Always on once the
/// secret cabinet is wired (no opt-in flag) because draining a scheduled
/// retirement is a correctness requirement, not an optional feature —
/// without it the old credential never reaches <c>Revoked</c>.</para>
/// </summary>
public sealed class RetireSweepHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<RetireSweepOptions> _options;
    private readonly ILogger<RetireSweepHostedService> _logger;

    public RetireSweepHostedService(
        IServiceProvider services,
        IOptions<RetireSweepOptions> options,
        ILogger<RetireSweepHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        _logger.LogInformation(
            "RetireSweepHostedService running poll={PollSeconds}s — active retire-tail drainer "
            + "(PlatformTaskWorker:RunOnStartup may stay false).",
            opts.PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RetireSweepHostedService tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("RetireSweepHostedService shut down.");
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();

        // No Postgres secret cabinet wired (dev / test without the cabinet)
        // ⇒ there can be no retire rows to drain. Not an error — skip.
        var dbFactory = scope.ServiceProvider
            .GetService<IDbContextFactory<SecretsDbContext>>();
        if (dbFactory is null) return;

        var scheduler = scope.ServiceProvider.GetRequiredService<IRetireScheduler>();
        var processed = await scheduler.SweepDueRetireTasksAsync(ct).ConfigureAwait(false);
        if (processed > 0)
        {
            _logger.LogInformation(
                "secret.retire.sweep drained={Drained}", processed);
        }
    }
}
