using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Data;

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
/// (<c>pg_try_advisory_lock</c> on the CP connection keyed by the hour) and
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

        // Multi-pod leader election on the CP connection: only the pod that wins
        // pg_try_advisory_lock for this hour writes checkpoints. Non-Postgres
        // (tests) → single-pod, always leader.
        var lockKey = AdvisoryLockBase ^ ((long)hourKey.Year << 20) ^ (hourKey.DayOfYear * 64L + hourKey.Hour);
        var isPg = cp.Database.IsNpgsql();
        var conn = isPg ? (NpgsqlConnection)cp.Database.GetDbConnection() : null;
        var opened = false;
        var acquired = !isPg;

        try
        {
            if (isPg && conn is not null)
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    opened = true;
                }
                await using var lockCmd = conn.CreateCommand();
                lockCmd.CommandText = "SELECT pg_try_advisory_lock(@k);";
                lockCmd.Parameters.AddWithValue("k", lockKey);
                acquired = (bool?)await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == true;
            }

            if (!acquired)
            {
                _lastFired = hourKey; // another pod owns this hour
                _logger.LogInformation(
                    "audit.chain.checkpoint.skipped_not_leader hour={Hour}", $"{now:yyyy-MM-dd HH:00}");
                return;
            }

            var checkpoints = scope.ServiceProvider.GetRequiredService<IAuditChainCheckpointService>();
            var written = await checkpoints.WriteAllActiveScopesAsync(ct).ConfigureAwait(false);
            _lastFired = hourKey;
            _logger.LogInformation(
                "audit.chain.checkpoint.tick_complete hour={Hour} checkpointsWritten={Written}",
                $"{now:yyyy-MM-dd HH:00}", written);
        }
        finally
        {
            if (isPg && conn is not null && acquired)
            {
                try
                {
                    await using var unlock = conn.CreateCommand();
                    unlock.CommandText = "SELECT pg_advisory_unlock(@k);";
                    unlock.Parameters.AddWithValue("k", lockKey);
                    await unlock.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }
                catch { /* closing the connection releases it anyway */ }
            }
            if (opened && conn is not null)
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAllAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IAuditChainCheckpointService>();
        await checkpoints.WriteAllActiveScopesAsync(ct).ConfigureAwait(false);
    }
}
