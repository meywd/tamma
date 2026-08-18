using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Options for <see cref="AdlLoopWatchdogService"/>. Bound to the <c>Adl:Watchdog</c>
/// configuration section.
/// </summary>
public sealed class AdlLoopWatchdogOptions
{
    public const string SectionName = "Adl:Watchdog";

    /// <summary>
    /// Master switch. Default <c>true</c>: the watchdog is inert on a host that never
    /// ran the loop (see <see cref="AdlLoopWatchdogService"/>'s "has the loop ever run"
    /// guard), so leaving it on costs one cheap COUNT per poll and nothing else.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long the loop may show no live <c>adl-orchestrator</c> instance before it is
    /// declared stalled. Default 10 minutes. It does NOT need to exceed the ADL cooldown:
    /// during a cooldown the orchestrator is suspended on a timer bookmark and therefore
    /// still counts as Running, so a long cooldown produces no false positive.
    /// </summary>
    public TimeSpan StallThreshold { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often to check. Default 2 minutes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether a detected stall should be repaired by dispatching a fresh orchestrator.
    /// Default <c>true</c>. When false (or when the operator stop switch is engaged) the
    /// stall is still reported loudly — the watchdog's first job is to make silent death
    /// impossible; re-arming is the second.
    /// </summary>
    public bool ReArm { get; set; } = true;

    /// <summary>
    /// Config seed used to re-arm when this host has no cached live config (a stall
    /// observed after a cold start). Optional; without it — and without a cached config —
    /// the watchdog reports the stall but refuses to re-arm, because restarting the loop
    /// against the DEFAULT repository is worse than leaving it down for a human.
    /// </summary>
    public string? ConfigJson { get; set; }
}

/// <summary>
/// LOOP WATCHDOG — makes a permanently-stopped autonomous loop impossible to miss, and
/// (by default) repairs it.
///
/// <para><b>The failure it covers.</b> <c>adl-orchestrator</c> restarts itself and nothing
/// else dispatches it. <see cref="DispatchAdlActivity"/> retries and, on giving up, writes
/// a Critical log line plus a durable <c>ADL.SELF.DISPATCH.FAILED</c> event — but both of
/// those depend on the very infrastructure that just failed (the event drain needs the API
/// that may be down; the log needs someone reading it). Every other way the chain can break
/// — the host dying between the cooldown timer and the restart edge, a deploy mid-tick, the
/// instance being cancelled by hand — leaves no signal at all. This service is the
/// out-of-band observer that does not share those failure modes.</para>
///
/// <para><b>Detection.</b> Two COUNT queries, no ordering or paging:</para>
/// <list type="number">
///   <item><description>live instances of <c>adl-orchestrator</c> (Elsa's
///     <c>WorkflowStatus.Running</c> covers both executing AND suspended-on-bookmark, so a
///     cooldown-suspended orchestrator reads as alive);</description></item>
///   <item><description>instances of any status — zero means the loop was never started on
///     this deployment, so there is nothing to watch and nothing to restart. Without this
///     guard the watchdog would start a loop nobody asked for.</description></item>
/// </list>
/// <para>A live instance stamps the liveness clock. When the clock has been stale for
/// longer than <see cref="AdlLoopWatchdogOptions.StallThreshold"/>, the loop is stalled.</para>
///
/// <para><b>Escalation.</b> Always LogCritical + a durable error-status
/// <c>ADL.LOOP.STALLED</c> DCB event. Then re-arm (<c>ADL.LOOP.REARMED</c>) unless the
/// operator stop switch is engaged, re-arm is disabled, or no config seed is available —
/// each of which is itself recorded as <c>ADL.LOOP.REARM_SKIPPED</c> (error status: the
/// loop is still down).</para>
///
/// <para>Re-arm is one-shot per stall: the liveness clock is stamped after a dispatch so a
/// broken loop is not re-dispatched every poll interval.</para>
/// </summary>
public sealed class AdlLoopWatchdogService : BackgroundService
{
    /// <summary>The definition this watchdog guards.</summary>
    public const string DefinitionId = "adl-orchestrator";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowDispatcher _dispatcher;
    private readonly IOptions<AdlLoopWatchdogOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdlLoopWatchdogService> _logger;
    private readonly IConfiguration? _configuration;
    private readonly AdlLoopConfigCache? _configCache;
    private readonly IAdlStopSwitch _stopSwitch;

    private DateTimeOffset _lastAliveUtc;

    public AdlLoopWatchdogService(
        IServiceScopeFactory scopeFactory,
        IWorkflowDispatcher dispatcher,
        IOptions<AdlLoopWatchdogOptions> options,
        TimeProvider timeProvider,
        ILogger<AdlLoopWatchdogService> logger,
        IConfiguration? configuration = null,
        AdlLoopConfigCache? configCache = null,
        IAdlStopSwitch? stopSwitch = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _dispatcher = dispatcher;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _configuration = configuration;
        _configCache = configCache;
        _stopSwitch = stopSwitch ?? new ConfigAdlStopSwitch(configuration);
        _lastAliveUtc = timeProvider.GetUtcNow();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation("AdlLoopWatchdog disabled — a stopped autonomous loop will NOT be detected.");
            return;
        }

        _logger.LogInformation(
            "AdlLoopWatchdog running stallThreshold={StallMinutes}m poll={PollSeconds}s reArm={ReArm}",
            opts.StallThreshold.TotalMinutes, opts.PollInterval.TotalSeconds, opts.ReArm);

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
                _logger.LogWarning(ex, "AdlLoopWatchdog tick threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("AdlLoopWatchdog shut down.");
    }

    /// <summary>Test entry point — drives one tick without spinning the BackgroundService loop.</summary>
    internal Task InvokeTickForTestsAsync(CancellationToken ct) => TickAsync(_options.Value, ct);

    /// <summary>Test seam — pretend the loop was last seen alive at this instant.</summary>
    internal void SetLastAliveForTests(DateTimeOffset when) => _lastAliveUtc = when;

    private async Task TickAsync(AdlLoopWatchdogOptions opts, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetService<IWorkflowInstanceStore>();
        if (store is null)
        {
            _logger.LogWarning("AdlLoopWatchdog: no IWorkflowInstanceStore — cannot observe loop liveness.");
            return;
        }

        var live = await store.CountAsync(
            new WorkflowInstanceFilter { DefinitionId = DefinitionId, WorkflowStatus = WorkflowStatus.Running },
            ct).ConfigureAwait(false);

        if (live > 0)
        {
            _lastAliveUtc = now;
            return;
        }

        // No live instance. Has the loop EVER run here? If not, this deployment simply
        // does not use the autonomous loop and the watchdog must stay out of the way.
        var everRan = await store.CountAsync(
            new WorkflowInstanceFilter { DefinitionId = DefinitionId }, ct).ConfigureAwait(false);
        if (everRan == 0) return;

        var down = now - _lastAliveUtc;
        if (down < opts.StallThreshold) return;

        // A loop a human deliberately stopped is not a stall. Reporting it as one every
        // threshold window would train operators to ignore ADL.LOOP.STALLED, which is the
        // one event that has to mean something.
        var stopReason = _stopSwitch.GetStopReason();
        if (stopReason is not null)
        {
            _lastAliveUtc = now;
            _logger.LogInformation(
                "adl.loop.down_by_operator reason={Reason} — not reported as a stall.", stopReason);
            return;
        }

        // ── Stalled ────────────────────────────────────────────────────────────
        var downMinutes = (int)down.TotalMinutes;
        _logger.LogCritical(
            "adl.loop.stalled — no live {DefinitionId} instance for {DownMinutes}m (threshold {ThresholdMinutes}m). "
            + "The autonomous loop has STOPPED.",
            DefinitionId, downMinutes, (int)opts.StallThreshold.TotalMinutes);

        await EmitAsync(scope, AdlLoopEvents.LoopStalled, "error", new Dictionary<string, object?>
        {
            ["downMinutes"] = downMinutes,
            ["thresholdMinutes"] = (int)opts.StallThreshold.TotalMinutes,
            ["definitionId"] = DefinitionId,
        }, ct).ConfigureAwait(false);

        var skipReason = ResolveSkipReason(opts, out var configJson);
        if (skipReason is not null)
        {
            _logger.LogCritical(
                "adl.loop.rearm_skipped reason={Reason} — the loop stays DOWN until a human acts.", skipReason);
            await EmitAsync(scope, AdlLoopEvents.LoopReArmSkipped, "error", new Dictionary<string, object?>
            {
                ["reason"] = skipReason,
                ["downMinutes"] = downMinutes,
            }, ct).ConfigureAwait(false);

            // Stamp the clock so a permanently-skipped stall reports once per threshold
            // window rather than once per poll.
            _lastAliveUtc = now;
            return;
        }

        try
        {
            var definitionVersionId = await PublishedWorkflowDispatch.ResolvePublishedVersionIdAsync(
                scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionService>(), DefinitionId, ct)
                .ConfigureAwait(false);

            var request = new DispatchWorkflowDefinitionRequest(definitionVersionId)
            {
                Input = new Dictionary<string, object> { ["configJson"] = configJson! },
            };

            await _dispatcher.DispatchAsync(request, new DispatchWorkflowOptions(), ct).ConfigureAwait(false);

            // One-shot per stall: treat the re-arm as liveness so a loop that cannot start
            // is not re-dispatched every poll interval.
            _lastAliveUtc = now;

            _logger.LogWarning(
                "adl.loop.rearmed — dispatched a fresh {DefinitionId} after {DownMinutes}m down.",
                DefinitionId, downMinutes);
            await EmitAsync(scope, AdlLoopEvents.LoopReArmed, "success", new Dictionary<string, object?>
            {
                ["downMinutes"] = downMinutes,
                ["definitionId"] = DefinitionId,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "adl.loop.rearm_failed — the autonomous loop is STOPPED and could not be restarted.");
            await EmitAsync(scope, AdlLoopEvents.LoopReArmSkipped, "error", new Dictionary<string, object?>
            {
                ["reason"] = "rearm dispatch threw",
                ["exception"] = ex.GetType().Name,
                ["message"] = ex.Message,
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reason NOT to re-arm, or null when re-arm should proceed (in which case
    /// <paramref name="configJson"/> carries the config to restart with).
    /// </summary>
    private string? ResolveSkipReason(AdlLoopWatchdogOptions opts, out string? configJson)
    {
        configJson = null;

        // The stop switch is checked earlier, before the stall is even declared — an
        // operator-stopped loop never reaches here.
        if (!opts.ReArm) return "re-arm disabled (Adl:Watchdog:ReArm=false)";

        configJson = _configCache?.Last
                     ?? (string.IsNullOrWhiteSpace(opts.ConfigJson) ? null : opts.ConfigJson)
                     ?? _configuration?.GetValue<string?>("Adl:ConfigJson");

        return string.IsNullOrWhiteSpace(configJson)
            ? "no config seed — set Adl:Watchdog:ConfigJson so the loop can be restarted with its real repository"
            : null;
    }

    /// <summary>
    /// Persist a loop-liveness event straight into the DCB stream. The watchdog runs
    /// outside any workflow, so it cannot use the activity event emitter's transient list
    /// — it posts through the same <c>POST /api/engine/events</c> endpoint the drain uses.
    /// Best-effort: a failure here is logged, never thrown (the Critical log above is the
    /// backstop for the backstop).
    /// </summary>
    private async Task EmitAsync(
        IServiceScope scope, string eventType, string status, Dictionary<string, object?> data, CancellationToken ct)
    {
        try
        {
            var api = scope.ServiceProvider.GetService<TammaApiClient>();
            if (api is null) return;

            var evt = new TammaEvent
            {
                EventType = eventType,
                Status = status,
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                ActivityName = nameof(AdlLoopWatchdogService),
                Data = data,
                Tags = new Dictionary<string, object?>
                {
                    ["component"] = "adl-orchestrator",
                    ["loopStopped"] = status == "error" ? "true" : "false",
                },
            };

            await api.AppendEventsAsync(
                new[] { EventPersistenceMiddleware.ToWireRecord(evt) }, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AdlLoopWatchdog could not persist {EventType} — the Critical log stands.", eventType);
        }
    }
}
