using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    // Track the (year, day-of-year, hour) of the most recent successful
    // dispatch so a poll-interval that overlaps the fire minute doesn't
    // double-dispatch. Reset on process restart — the workflow's UPSERT
    // path covers the post-restart "did the last hour fire" case.
    private (int Year, int DayOfYear, int Hour) _lastFired;

    public HourlyAnalyticsRollupScheduler(
        IWorkflowDispatcher dispatcher,
        IOptions<HourlyAnalyticsRollupSchedulerOptions> options,
        TimeProvider timeProvider,
        ILogger<HourlyAnalyticsRollupScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
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
                "analytics.rollup.dispatched hour={Hour} instance={InstanceId}",
                $"{now:yyyy-MM-dd HH:00}",
                instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "analytics.rollup.dispatch_failed hour={Hour} — next fire is {NextHour}",
                $"{now:yyyy-MM-dd HH:00}",
                $"{now.AddHours(1):yyyy-MM-dd HH:00}");
        }
    }
}
