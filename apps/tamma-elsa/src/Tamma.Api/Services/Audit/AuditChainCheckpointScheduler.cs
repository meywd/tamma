using Microsoft.EntityFrameworkCore;
using Tamma.Data;
using Tamma.Data.Pooling;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC6) — options for <see cref="AuditChainCheckpointScheduler"/>.
/// </summary>
public sealed class AuditChainCheckpointOptions
{
    public const string SectionName = "AuditChainCheckpoint";

    /// <summary>
    /// When <c>true</c> the scheduler's loop runs once the host starts. Default
    /// <c>false</c> (opt-in), mirroring <see cref="AuditProjectorOptions.RunOnStartup"/>
    /// so the loop does not fire during tests / un-opted deployments.
    /// </summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Minute of the hour (UTC) to write checkpoints. Default 15.</summary>
    public int FireAtMinute { get; set; } = 15;

    /// <summary>Clock poll cadence. Default 30s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Story 37-2 (AC6) — periodically writes one signed checkpoint per active
/// scope. Structurally the <c>AuditProjectorBackgroundService</c> host (per-tick
/// DI scope, <c>RunOnStartup</c> gate, WARN-and-continue) combined with the
/// <c>HourlyAnalyticsRollupScheduler</c>'s multi-pod leader election
/// (<c>pg_try_advisory_lock</c> keyed by the hour, on a dedicated NON-POOLED
/// session via <see cref="Tamma.Data.Pooling.PostgresAdvisoryLock"/>) and
/// <c>_lastFired</c> hour dedup.
///
/// <para><b>Placement note.</b> The spec sketched this as an Elsa-dispatched
/// workflow in <c>Tamma.ElsaServer</c>; but the checkpoint logic
/// (<see cref="IAuditChainCheckpointService"/>) lives in <c>Tamma.Api</c>, which
/// the Elsa host / activities layer does not (and must not) reference. So — like
/// the audit projector host — it runs as a Tamma.Api <see cref="BackgroundService"/>
/// that invokes the service directly. On-demand checkpointing reuses the same
/// service from the admin endpoint.</para>
/// </summary>
public sealed class AuditChainCheckpointScheduler : BackgroundService
{
    // "TAUD" high-half namespace + "CKPT" low-half — greppable in pg_locks.
    private const long AdvisoryLockBase = (0x5441_5544L << 32) | 0x434B_5054L;

    private readonly IServiceProvider _services;
    private readonly AuditChainCheckpointOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditChainCheckpointScheduler> _logger;

    private (int Year, int DayOfYear, int Hour) _lastFired;

    public AuditChainCheckpointScheduler(
        IServiceProvider services,
        AuditChainCheckpointOptions options,
        TimeProvider clock,
        ILogger<AuditChainCheckpointScheduler> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunOnStartup)
        {
            _logger.LogDebug(
                "AuditChainCheckpointScheduler gated off (RunOnStartup=false); loop will not start.");
            return;
        }

        _logger.LogInformation(
            "AuditChainCheckpointScheduler running fireAtMinute={Minute} poll={PollSeconds}s",
            _options.FireAtMinute, _options.PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AuditChainCheckpointScheduler tick threw; continuing.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("AuditChainCheckpointScheduler shut down.");
    }

    /// <summary>Test-only single-tick driver (bypasses the loop + fire-minute gate).</summary>
    public Task RunOnceAsync(CancellationToken ct) => WriteAllAsync(ct);

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (now.Minute < _options.FireAtMinute) return;
        var hourKey = (now.Year, now.DayOfYear, now.Hour);
        if (hourKey == _lastFired) return;

        using var scope = _services.CreateScope();
        var cp = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        // Multi-pod leader election: only the pod that wins
        // pg_try_advisory_lock for this hour writes checkpoints. Non-Postgres
        // (tests) → single-pod, always leader.
        //
        // 2026-07-30 audit — the lock is taken on its own NON-POOLED session,
        // NOT on this scope's pooled CP connection. It used to ride the CP
        // context's pooled connector, and the unlock in the finally was passed
        // the tick's OWN CancellationToken: on host shutdown, ct is already
        // cancelled when the finally runs, so the unlock threw immediately,
        // was swallowed by a bare catch, and the connector went back to the
        // pool with the hour's lock STILL HELD (Npgsql defers the DISCARD ALL
        // that releases it until that connector is next used). Every pod then
        // saw "another pod is leader" and skipped, so that hour got no audit
        // checkpoints from anyone. See PostgresAdvisoryLock.
        var lockKey = AdvisoryLockBase ^ ((long)hourKey.Year << 20) ^ (hourKey.DayOfYear * 64L + hourKey.Hour);
        var isPg = cp.Database.IsNpgsql();

        PostgresAdvisoryLockLease? lease = null;
        if (isPg)
        {
            var connectionString = cp.Database.GetConnectionString()
                ?? throw new InvalidOperationException(
                    "The control-plane context exposes no connection string, so the "
                    + "audit-checkpoint leader lock cannot be taken on a dedicated session.");

            // Same key, same pg_try_advisory_lock(bigint) call as before.
            lease = await PostgresAdvisoryLock.TryAcquireAsync(
                connectionString,
                PostgresAdvisoryLockKey.FromInt64(lockKey),
                _logger,
                ct).ConfigureAwait(false);

            if (lease is null)
            {
                _lastFired = hourKey; // another pod owns this hour
                _logger.LogInformation(
                    "audit.chain.checkpoint.skipped_not_leader hour={Hour}", $"{now:yyyy-MM-dd HH:00}");
                return;
            }
        }

        await using (lease)
        {
            var checkpoints = scope.ServiceProvider.GetRequiredService<IAuditChainCheckpointService>();
            var written = await checkpoints.WriteAllActiveScopesAsync(ct).ConfigureAwait(false);
            _lastFired = hourKey;
            _logger.LogInformation(
                "audit.chain.checkpoint.tick_complete hour={Hour} checkpointsWritten={Written}",
                $"{now:yyyy-MM-dd HH:00}", written);
        }
    }

    private async Task WriteAllAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAuditChainCheckpointService>();
        await checkpoints.WriteAllActiveScopesAsync(ct).ConfigureAwait(false);
    }
}
