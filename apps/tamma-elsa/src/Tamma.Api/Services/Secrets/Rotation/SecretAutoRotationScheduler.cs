using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Options for <see cref="SecretAutoRotationScheduler"/>. Bound to the
/// <c>SecretAutoRotation</c> configuration section.
/// </summary>
public sealed class SecretAutoRotationSchedulerOptions
{
    public const string SectionName = "SecretAutoRotation";

    /// <summary>
    /// When <c>true</c> the scheduler dispatches the <c>rotate-secret</c>
    /// workflow for each due secret on the poll cadence. <b>Default
    /// <c>false</c></b> — an operator opts in once the Elsa engine and
    /// rotation handlers are deployed (the workflow is dispatched over
    /// HTTP to the engine). Tests + non-rotation composition roots leave
    /// this off so no background loop spawns.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How often the scheduler scans for due secrets. Default 5 minutes
    /// — auto-rotation is a low-frequency cadence (intervals are in
    /// days), so a coarse poll keeps the DB scan cheap.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default retire grace window (seconds) handed to each dispatched
    /// rotation. 0 ⇒ the saga's own default (15 min).
    /// </summary>
    public long GraceWindowSeconds { get; set; }
}

/// <summary>
/// Story 29-6 (audit gap #2b) — scheduled auto-rotation. Mirrors the
/// <c>HourlyAnalyticsRollupScheduler</c> shape: a lightweight
/// poll-the-clock <see cref="BackgroundService"/> that selects secrets
/// whose <c>NextRotationDueAt</c> has elapsed and dispatches the
/// <c>rotate-secret</c> workflow for each with
/// <c>operatorUserId = Guid.Empty</c> (system actor).
///
/// <para><b>No empty/plain fallback</b>: a due secret with no consumer /
/// handler is still dispatched — the saga emits
/// <c>SECRET.ROTATION.FAILED(handler_not_registered)</c> (an honest,
/// audited failure) rather than being silently skipped.</para>
///
/// <para><b>Double-dispatch safety</b>: the
/// <see cref="IRotationTriggerService"/> takes the per-secret concurrency
/// guard before dispatch, so an in-flight rotation (operator click or a
/// prior tick still running) is rejected with
/// <c>SECRET.ROTATION.REJECTED(rotation_in_progress)</c> rather than
/// double-pushed.</para>
///
/// <para>The clock is the only state — <c>NextRotationDueAt</c> moves
/// forward only after the saga activates (the gateway stamps
/// <c>LastRotatedAt</c>; the metadata layer recomputes the next-due), so
/// a crash mid-rotation re-selects the same secret on the next tick and
/// the guard keeps it from stacking.</para>
/// </summary>
public sealed class SecretAutoRotationScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<SecretAutoRotationSchedulerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SecretAutoRotationScheduler> _logger;

    public SecretAutoRotationScheduler(
        IServiceProvider services,
        IOptions<SecretAutoRotationSchedulerOptions> options,
        TimeProvider timeProvider,
        ILogger<SecretAutoRotationScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogDebug(
                "SecretAutoRotationScheduler disabled (Enabled=false) — not scanning for due secrets.");
            return;
        }

        _logger.LogInformation(
            "SecretAutoRotationScheduler running poll={PollSeconds}s grace={Grace}s",
            opts.PollInterval.TotalSeconds, opts.GraceWindowSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(opts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SecretAutoRotationScheduler tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("SecretAutoRotationScheduler shut down.");
    }

    /// <summary>
    /// Test-only entry point so unit tests can drive a single scan
    /// without spinning the BackgroundService loop. Returns the number of
    /// secrets for which a rotation was dispatched (accepted by the
    /// guard).
    /// </summary>
    internal Task<int> ScanOnceForTestsAsync(CancellationToken ct) =>
        TickAsync(_options.Value, ct);

    private async Task<int> TickAsync(
        SecretAutoRotationSchedulerOptions opts, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider
            .GetService<IDbContextFactory<SecretsDbContext>>();
        if (dbFactory is null)
        {
            // No Postgres secrets wired (dev / test without the cabinet) —
            // nothing to scan. Not an error.
            return 0;
        }
        var trigger = scope.ServiceProvider.GetRequiredService<IRotationTriggerService>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        List<Guid> dueIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            dueIds = await db.Secrets
                .AsNoTracking()
                .Where(s => s.NextRotationDueAt != null && s.NextRotationDueAt <= now)
                .Select(s => s.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        if (dueIds.Count == 0) return 0;

        var dispatched = 0;
        foreach (var secretId in dueIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await trigger.TriggerRotationAsync(
                    secretId,
                    operatorUserId: Guid.Empty,
                    newPlaintext: null,        // saga CSPRNG-generates
                    generateLength: null,      // saga default length
                    graceWindowSeconds: opts.GraceWindowSeconds,
                    ct: ct).ConfigureAwait(false);
                if (result.Accepted) dispatched++;
            }
            catch (Exception ex)
            {
                // One secret's dispatch failure must not abort the scan —
                // the next tick re-selects it.
                _logger.LogWarning(ex,
                    "secret.auto_rotation.dispatch_failed secret={Secret}", secretId);
            }
        }

        _logger.LogInformation(
            "secret.auto_rotation.scan due={Due} dispatched={Dispatched}",
            dueIds.Count, dispatched);
        return dispatched;
    }
}
