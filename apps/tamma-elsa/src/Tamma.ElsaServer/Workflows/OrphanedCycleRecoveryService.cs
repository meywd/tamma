using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;

namespace Tamma.ElsaServer.Workflows;

/// <summary>Options for <see cref="OrphanedCycleRecoveryService"/> (<c>Adl:OrphanRecovery</c>).</summary>
public sealed class OrphanedCycleRecoveryOptions
{
    public const string SectionName = "Adl:OrphanRecovery";

    /// <summary>Master switch. Default <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long an instance may sit mid-EXECUTION (not suspended on any bookmark) before
    /// it is treated as crash-orphaned. Default 90 minutes: the agent wait is
    /// <c>TimeoutMinutes</c> (default 30) plus discovery and the webhook safety window,
    /// so ~35 minutes is the longest legitimate stretch with no state write; 90 leaves
    /// headroom for a slow provider before anything is force-terminated.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>How often to sweep. Default 10 minutes. The first sweep runs at startup.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Definitions swept. Default: <c>single-issue-cycle</c> — the only workflow that
    /// hosts <c>ExecuteAgentActivity</c>'s long inline wait, and the one whose zombie
    /// instances hold an ADL <c>MaxConcurrent</c> slot.
    /// </summary>
    public string[] DefinitionIds { get; set; } = new[] { "single-issue-cycle" };
}

/// <summary>
/// ORPHANED-CYCLE RECOVERY — crash detection for the agent run's non-durable wait.
///
/// <para><b>The failure it covers.</b> <c>ExecuteAgentActivity</c> awaits the agent inline
/// (dispatch → discover → poll to terminal, up to ~35 minutes) with no bookmark and
/// nothing persisted between polls. A deploy or crash inside that window leaves the cycle
/// instance recorded as <c>Running</c>/<c>Executing</c> with no bookmark for the scheduler
/// to resume from: it never finishes, never faults, and is never reported. Worse, it is
/// still counted by <see cref="CheckLimitsActivity"/>, so with the default
/// <c>MaxConcurrent=1</c> a single orphan stops the autonomous loop from ever dispatching
/// another cycle. Full durability (a resumable bookmark around the agent wait) is story
/// 40-2; until then this sweep guarantees the orphan is DETECTED, AUDITED and CLEARED
/// rather than silently stranding the loop.</para>
///
/// <para><b>Detection.</b> Instances of the swept definitions in
/// <c>WorkflowStatus.Running</c> with <c>WorkflowSubStatus.Executing</c> — i.e. the store
/// believes an activity is mid-run — whose last write is older than
/// <see cref="OrphanedCycleRecoveryOptions.StaleAfter"/>. A workflow legitimately waiting
/// on a human, a webhook or a timer is <c>Suspended</c>, not <c>Executing</c>, so it is
/// never touched by this sweep.</para>
///
/// <para><b>Recovery.</b> Cancel the instance (freeing the concurrency slot) and record a
/// durable error-status <c>ADL.CYCLE.ORPHANED</c> event naming the instance and how long
/// it was stranded. Deliberately a LOUD failure rather than a re-attach: re-attaching to
/// an in-flight agent run means resuming mid-activity, which is precisely the durable
/// bookmark that 40-2 adds. The issue returns to the pool on the next selection pass
/// because nothing marked it complete.</para>
/// </summary>
public sealed class OrphanedCycleRecoveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OrphanedCycleRecoveryOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrphanedCycleRecoveryService> _logger;

    public OrphanedCycleRecoveryService(
        IServiceScopeFactory scopeFactory,
        IOptions<OrphanedCycleRecoveryOptions> options,
        TimeProvider timeProvider,
        ILogger<OrphanedCycleRecoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation("OrphanedCycleRecovery disabled — crash-orphaned cycles will hold their slots.");
            return;
        }

        _logger.LogInformation(
            "OrphanedCycleRecovery running staleAfter={StaleMinutes}m poll={PollSeconds}s definitions={Definitions}",
            opts.StaleAfter.TotalMinutes, opts.PollInterval.TotalSeconds, string.Join(",", opts.DefinitionIds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(opts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OrphanedCycleRecovery sweep threw; continuing.");
            }

            try
            {
                await Task.Delay(opts.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("OrphanedCycleRecovery shut down.");
    }

    /// <summary>Test entry point — one sweep without the BackgroundService loop.</summary>
    internal Task InvokeSweepForTestsAsync(CancellationToken ct) => SweepAsync(_options.Value, ct);

    private async Task SweepAsync(OrphanedCycleRecoveryOptions opts, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetService<IWorkflowInstanceStore>();
        if (store is null) return;

        var cutoff = _timeProvider.GetUtcNow() - opts.StaleAfter;

        foreach (var definitionId in opts.DefinitionIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(definitionId)) continue;

            var instances = await store.FindManyAsync(
                new WorkflowInstanceFilter
                {
                    DefinitionId = definitionId,
                    WorkflowStatus = WorkflowStatus.Running,
                    // Suspended instances are legitimately waiting on a bookmark (human
                    // gate, webhook, timer). Only "Executing" means the store believes an
                    // activity is mid-run — which, past the cutoff, means the host died
                    // inside one.
                    WorkflowSubStatus = WorkflowSubStatus.Executing,
                },
                ct).ConfigureAwait(false);

            foreach (var instance in instances)
            {
                if (LastWrite(instance) > cutoff) continue;
                await RecoverAsync(scope, instance, definitionId, cutoff, ct).ConfigureAwait(false);
            }
        }
    }

    private static DateTimeOffset LastWrite(WorkflowInstance instance)
        => instance.UpdatedAt > instance.CreatedAt ? instance.UpdatedAt : instance.CreatedAt;

    private async Task RecoverAsync(
        IServiceScope scope, WorkflowInstance instance, string definitionId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var strandedMinutes = (int)(_timeProvider.GetUtcNow() - LastWrite(instance)).TotalMinutes;

        _logger.LogError(
            "adl.cycle.orphaned instance={InstanceId} definition={DefinitionId} strandedMinutes={StrandedMinutes} — "
            + "the host died inside a non-durable agent wait; terminating so the cycle stops holding a concurrency slot.",
            instance.Id, definitionId, strandedMinutes);

        var cancelled = false;
        string? cancelError = null;
        try
        {
            var canceller = scope.ServiceProvider.GetService<IWorkflowCancellationService>();
            if (canceller is not null)
            {
                await canceller.CancelWorkflowsAsync(new[] { instance.Id }, ct).ConfigureAwait(false);
                cancelled = true;
            }
            else
            {
                cancelError = "IWorkflowCancellationService not registered";
            }
        }
        catch (Exception ex)
        {
            cancelError = ex.Message;
            _logger.LogError(ex, "Failed to terminate orphaned instance {InstanceId}", instance.Id);
        }

        await EmitAsync(scope, new Dictionary<string, object?>
        {
            ["workflowInstanceId"] = instance.Id,
            ["definitionId"] = definitionId,
            ["strandedMinutes"] = strandedMinutes,
            ["staleCutoffUtc"] = cutoff.UtcDateTime,
            ["terminated"] = cancelled,
            ["terminationError"] = cancelError,
            // Named so the operator knows what to look for: the agent run itself may
            // still be executing on the platform side; nothing here cancels it.
            ["reason"] = "host restart or crash inside the inline agent wait (no bookmark to resume from)",
        }, instance.Id, ct).ConfigureAwait(false);
    }

    private async Task EmitAsync(
        IServiceScope scope, Dictionary<string, object?> data, string instanceId, CancellationToken ct)
    {
        try
        {
            var api = scope.ServiceProvider.GetService<TammaApiClient>();
            if (api is null) return;

            var evt = new TammaEvent
            {
                EventType = AdlLoopEvents.CycleOrphaned,
                Status = "error",
                Error = "cycle orphaned by a host restart inside the inline agent wait",
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                ActivityName = nameof(OrphanedCycleRecoveryService),
                WorkflowInstanceId = instanceId,
                Data = data,
                Tags = new Dictionary<string, object?> { ["component"] = "single-issue-cycle" },
            };

            await api.AppendEventsAsync(
                new[] { EventPersistenceMiddleware.ToWireRecord(evt) }, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OrphanedCycleRecovery could not persist ADL.CYCLE.ORPHANED — the ERROR log stands.");
        }
    }
}
