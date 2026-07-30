using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Tamma.Data.Pooling;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Options for <see cref="HourlyAnalyticsRollupScheduler"/>. Bound to
/// <c>HourlyAnalyticsRollup</c> configuration section.
/// </summary>
public sealed class HourlyAnalyticsRollupSchedulerOptions
{
    public const string SectionName = "HourlyAnalyticsRollup";

    /// <summary>
    /// When <c>true</c> (default) the scheduler dispatches the
    /// workflow at the configured cron offset. Tests +
    /// non-Elsa-host composition roots set this to <c>false</c> to
    /// avoid spawning the background loop.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minute of the hour at which to fire (UTC). Default <c>5</c>
    /// to match <see cref="HourlyAnalyticsRollupWorkflow.CronExpression"/>
    /// (<c>0 5 * * * *</c> — five past every hour). The 5-minute offset
    /// gives upstream emitters time to flush <c>platform_events</c>
    /// for the closing hour before the rollup runs.
    /// </summary>
    public int FireAtMinute { get; set; } = 5;

    /// <summary>
    /// How often the scheduler polls the clock. Default 30 seconds —
    /// the worst-case extra latency between the scheduled minute and
    /// the actual fire is one poll interval.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Story 28-10 — wakes up periodically and dispatches the
/// <see cref="HourlyAnalyticsRollupWorkflow"/> at the configured cron
/// offset. Lightweight alternative to wiring a full Elsa cron-trigger
/// activity (which would require additional Elsa packages); good enough
/// for a once-per-hour cadence.
///
/// <para><b>Idempotency</b>: the scheduler tracks the last-fired hour
/// (UTC) so a clock-drift retry within the same hour is suppressed.
/// The workflow itself is also idempotent (per-row UPSERT against
/// <c>platform_analytics_hourly</c>) so a missed-fire from a process
/// restart auto-recovers on the next hour.</para>
///
/// <para>Round-2 H9 — multi-pod safe via Postgres
/// <c>pg_try_advisory_lock</c> keyed on the <c>(year, day_of_year,
/// hour)</c> triple. Only one pod gets the lock and dispatches; others
/// log "another pod is the leader for this hour" and skip. The lock is
/// released at the end of the dispatch handler. Without this, an
/// N-pod deploy fired N redundant Elsa workflow dispatches per hour;
/// the workflow's UPSERT-style idempotency hid the cost but the
/// duplicate work was real.</para>
///
/// <para><b>Failure isolation</b>: a dispatch failure is logged at
/// WARN and the scheduler continues — the next hour's fire is the
/// recovery path, not a tight retry loop.</para>
/// </summary>
public sealed class HourlyAnalyticsRollupScheduler : BackgroundService
{
    private readonly IWorkflowDispatcher _dispatcher;
    private readonly IOptions<HourlyAnalyticsRollupSchedulerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HourlyAnalyticsRollupScheduler> _logger;
    private readonly IConfiguration? _configuration;
    private readonly IRollupSchedulerLeaderLock _leaderLock;

    // Track the (year, day-of-year, hour) of the most recent successful
    // dispatch so a poll-interval that overlaps the fire minute doesn't
    // double-dispatch. Reset on process restart — the workflow's UPSERT
    // path covers the post-restart "did the last hour fire" case.
    private (int Year, int DayOfYear, int Hour) _lastFired;

    public HourlyAnalyticsRollupScheduler(
        IWorkflowDispatcher dispatcher,
        IOptions<HourlyAnalyticsRollupSchedulerOptions> options,
        TimeProvider timeProvider,
        ILogger<HourlyAnalyticsRollupScheduler> logger,
        IConfiguration? configuration = null,
        IRollupSchedulerLeaderLock? leaderLock = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _configuration = configuration;
        // If no leader lock is injected, use the Postgres advisory-lock
        // implementation pulled from the DefaultConnection. Tests inject
        // a deterministic in-memory implementation.
        _leaderLock = leaderLock ?? new PostgresAdvisoryLeaderLock(configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "HourlyAnalyticsRollupScheduler disabled — skipping background dispatch.");
            return;
        }

        _logger.LogInformation(
            "HourlyAnalyticsRollupScheduler running fireAtMinute={Minute} poll={PollSeconds}s",
            opts.FireAtMinute,
            opts.PollInterval.TotalSeconds);

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
                    "HourlyAnalyticsRollupScheduler tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("HourlyAnalyticsRollupScheduler shut down.");
    }

    /// <summary>
    /// Test-only entry point so unit tests can drive a single tick
    /// without spinning the BackgroundService loop.
    /// <see cref="HourlyAnalyticsRollupScheduler"/>'s
    /// <c>InternalsVisibleTo</c> for <c>Tamma.Activities.Tests</c> in the
    /// ElsaServer project gives the test project access. Production
    /// code keeps using the private <c>TickAsync</c> via
    /// <see cref="ExecuteAsync"/>.
    /// </summary>
    internal Task InvokeTickForTestsAsync(CancellationToken ct)
        => TickAsync(_options.Value, ct);

    private async Task TickAsync(
        HourlyAnalyticsRollupSchedulerOptions opts,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        // Are we past the fire-minute for this hour AND haven't fired
        // for this hour yet?
        if (now.Minute < opts.FireAtMinute) return;
        var hourKey = (now.Year, now.DayOfYear, now.Hour);
        if (hourKey == _lastFired) return;

        // Round-2 H9 — multi-pod leader election via
        // pg_try_advisory_lock. Lock id is a 64-bit hash of the
        // (year, day_of_year, hour) triple so each hour gets its own
        // lock and one stuck pod doesn't poison the next hour's
        // dispatch. The hash is deterministic so every pod competing
        // for the same hour computes the same key.
        var lockKey = ComputeAdvisoryLockKey(hourKey.Year, hourKey.DayOfYear, hourKey.Hour);

        await using var lease = await _leaderLock.TryAcquireAsync(lockKey, ct)
            .ConfigureAwait(false);
        if (lease is null)
        {
            // Another pod is the leader for this hour. Mark the hour
            // as "handled" locally so we don't keep retrying inside
            // this hour's window. Without this, every poll-interval
            // tick would race the lock again and add log noise.
            _lastFired = hourKey;
            _logger.LogInformation(
                "analytics.rollup.skipped_not_leader hour={Hour} lockKey={LockKey}",
                $"{now:yyyy-MM-dd HH:00}",
                lockKey);
            return;
        }

        var instanceId = Guid.NewGuid().ToString();
        var request = new DispatchWorkflowDefinitionRequest(
            HourlyAnalyticsRollupWorkflow.DefinitionId)
        {
            InstanceId = instanceId,
            // No input variables — the workflow infers the target hour
            // from the current clock.
        };

        try
        {
            // Newer Elsa versions take a DispatchWorkflowOptions as the
            // second parameter (cancellation token lives in options).
            // The empty-options default keeps the call shape minimal.
            await _dispatcher.DispatchAsync(request, new DispatchWorkflowOptions(), ct)
                .ConfigureAwait(false);
            _lastFired = hourKey;
            _logger.LogInformation(
                "analytics.rollup.dispatched hour={Hour} instance={InstanceId} lockKey={LockKey}",
                $"{now:yyyy-MM-dd HH:00}",
                instanceId,
                lockKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "analytics.rollup.dispatch_failed hour={Hour} — next fire is {NextHour}",
                $"{now:yyyy-MM-dd HH:00}",
                $"{now.AddHours(1):yyyy-MM-dd HH:00}");
            // The lease's DisposeAsync will release the advisory lock
            // even on failure so a follow-up retry on the next pod is
            // unblocked.
        }
    }

    /// <summary>
    /// Round-2 H9 — derive a stable 64-bit lock id from the
    /// <c>(year, day_of_year, hour)</c> triple. Pure mathematical mix
    /// (no allocations) so two pods racing for the same hour compute
    /// the same key. Postgres advisory locks accept any
    /// <c>BIGINT</c>; collisions across years are theoretical only
    /// because we mod the year into a small range and OR the day +
    /// hour into the lower 32 bits.
    /// </summary>
    internal static long ComputeAdvisoryLockKey(int year, int dayOfYear, int hour)
    {
        // Layout: high 32 bits = year (with a fixed prefix that lets
        // ops grep the lock-id namespace in pg_locks); low 32 bits =
        // day_of_year * 64 + hour. The prefix 0x52_4C_55_50 is the
        // ASCII bytes for "RLUP" (rollup) — a hint to humans
        // diagnosing pg_locks output that this lock is owned by the
        // rollup scheduler.
        unchecked
        {
            long high = ((long)0x524C5550) ^ year;
            long low = ((long)dayOfYear * 64L) + hour;
            return (high << 32) | (low & 0xFFFFFFFFL);
        }
    }
}

/// <summary>
/// Round-2 H9 — abstraction over the leader-election primitive. The
/// production implementation
/// (<see cref="PostgresAdvisoryLeaderLock"/>) wraps
/// <c>pg_try_advisory_lock</c>; tests inject a deterministic in-memory
/// implementation.
/// </summary>
public interface IRollupSchedulerLeaderLock
{
    /// <summary>
    /// Attempt to acquire the advisory lock for <paramref name="lockKey"/>.
    /// Returns a lease whose <see cref="IAsyncDisposable.DisposeAsync"/>
    /// releases the lock; or <c>null</c> if the lock is currently held
    /// by another pod (in which case the caller skips this hour).
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(long lockKey, CancellationToken ct);
}

/// <summary>
/// Round-2 H9 — Postgres-backed leader-election lock that uses
/// <c>pg_try_advisory_lock(bigint)</c> on a transient, NON-POOLED
/// <see cref="NpgsqlConnection"/>. The lock is session-scoped — once
/// the connection closes, the lock auto-releases.
///
/// <para><b>2026-07-30 audit.</b> "Once the connection closes, the lock
/// auto-releases" used to be false here: the lease opened
/// <c>new NpgsqlConnection(cs)</c> against a plain connection string, so
/// the connection was POOLED, and disposing it returned the connector to
/// the pool with the backend session — and the hour's lock — still alive.
/// The unlock in the lease's dispose was swallowed on failure "because
/// closing the connection releases the lock either way", which was
/// exactly the false invariant. A swallowed unlock parked that hour's
/// leader lock shut, so every pod skipped the hour and the rollup for it
/// was never dispatched by anyone (the workflow infers its target hour
/// from the clock, so a skipped hour is not backfilled). Acquisition now
/// goes through <see cref="PostgresAdvisoryLock"/>, which opens a
/// <c>Pooling=false</c> session; the key and the
/// acquired/refused/throwing contract are unchanged.</para>
/// </summary>
internal sealed class PostgresAdvisoryLeaderLock : IRollupSchedulerLeaderLock
{
    private readonly IConfiguration? _configuration;

    public PostgresAdvisoryLeaderLock(IConfiguration? configuration)
    {
        _configuration = configuration;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        long lockKey, CancellationToken ct)
    {
        var cs = _configuration?.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            // No DB → can't lock. Fall through to "we're the leader"
            // (single-pod mode) so unit tests + dev environments
            // without Postgres still dispatch.
            return new NoOpLease();
        }

        // Same key, same pg_try_advisory_lock(bigint) call; null still
        // means "another pod holds this hour", a throw still propagates.
        return await PostgresAdvisoryLock.TryAcquireAsync(
            cs, PostgresAdvisoryLockKey.FromInt64(lockKey), logger: null, ct)
            .ConfigureAwait(false);
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
