using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.ADL;

/// <summary>
/// Checks operational limits before dispatching the next issue cycle.
///
/// <para>Checks, in order:</para>
/// <list type="number">
///   <item><description>Operator stop switch (<see cref="IAdlStopSwitch"/>) — the brake a
///     human can pull mid-incident without a redeploy.</description></item>
///   <item><description>Per-instance emergency stop (the orchestrator config's
///     <c>limits.emergencyStop</c>).</description></item>
///   <item><description>Active instances &lt; max concurrent (queries the Elsa runtime).</description></item>
///   <item><description>Spend ceiling (<see cref="AdlSpendCeiling"/>) — the tenant budget
///     limit and/or the ADL-specific <c>Adl:MaxSpendUsd</c> cap.</description></item>
/// </list>
///
/// <para>Outcomes: <c>Continue</c> (within all limits) / <c>Stop</c> (a limit was reached).
/// Both are audited: <c>stopReason</c> lands on the <c>ADL.LIMITS.CHECK.COMPLETED</c> DCB
/// event, so why the loop stopped dispatching is answerable from the event stream.</para>
///
/// <para><b>Stop is not fatal.</b> Every ADL terminal path funnels into
/// <c>cooldown → DispatchAdl</c>, so the Stop edge skips ONE tick and the orchestrator
/// still restarts. That is what makes it safe for the spend check to fail CLOSED: a
/// transient budget-API outage costs a cycle, never the loop.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Check Limits",
    "Check concurrency, budget, and emergency stop before next dispatch",
    Kind = ActivityKind.Task
)]
[FlowNode("Continue", "Stop")]
public class CheckLimitsActivity : TammaOutcomeActivity
{
    public override string? EventType => "ADL.LIMITS.CHECK";

    private readonly IWorkflowInstanceStore? _workflowInstanceStore;
    private readonly IAdlStopSwitch? _stopSwitch;

    /// <summary>
    /// Transient-property key carrying whether the spend ceiling could actually be
    /// enforced this tick. Surfaced in <see cref="BuildEndData"/> so an operator can
    /// query "was the loop capped while it was running" rather than infer it.
    /// </summary>
    private const string EnforceableKey = "adl:limits:ceilingEnforceable";

    // --- Inputs ---

    [Input(Description = "Max concurrent SingleIssueCycle instances")]
    public Input<int> MaxConcurrent { get; set; } = new(1);

    [Input(Description = "Emergency stop flag")]
    public Input<bool> EmergencyStop { get; set; } = new(false);

    /// <summary>
    /// ADL-specific spend ceiling in USD for the current budget period. <c>0</c> falls
    /// back to <c>Adl:MaxSpendUsd</c>, then to "no ADL ceiling" (in which case only the
    /// tenant budget limit applies — and that is 0/unlimited by default).
    /// </summary>
    [Input(Description = "Spend ceiling in USD for the budget period (0 = fall back to Adl:MaxSpendUsd)")]
    public Input<decimal> MaxSpendUsd { get; set; } = new(0m);

    /// <summary>
    /// Budget bucket to meter against. Empty falls back to the workflow's
    /// <c>TenantId</c>/<c>AccountId</c> variable, then to <c>Adl:BudgetOwnerId</c> — the
    /// single-user path, where no tenant is ever stamped on the workflow.
    /// </summary>
    [Input(Description = "Budget owner id (GUID). Empty → workflow TenantId, then Adl:BudgetOwnerId")]
    public Input<string?> BudgetOwnerId { get; set; } = new((string?)null);

    // --- Outputs ---

    [Output(Description = "Reason for stopping, empty if continuing")]
    public Output<string?> StopReason { get; set; } = default!;

    [Output(Description = "Number of currently active cycle instances")]
    public Output<int> ActiveInstances { get; set; } = default!;

    /// <summary>Observed period spend in USD; 0 when no budget source could be read.</summary>
    [Output(Description = "Observed spend (USD) in the current budget period")]
    public Output<decimal> SpentUsd { get; set; } = default!;

    [JsonConstructor]
    public CheckLimitsActivity() { }

    public CheckLimitsActivity(
        ILogger<CheckLimitsActivity> logger,
        IWorkflowInstanceStore workflowInstanceStore,
        IAdlStopSwitch? stopSwitch = null)
    {
        Logger = logger;
        _workflowInstanceStore = workflowInstanceStore;
        _stopSwitch = stopSwitch;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var maxConcurrent = MaxConcurrent.Get(context);
        var emergencyStop = EmergencyStop.Get(context);

        // 1. Operator stop switch. Checked FIRST and before any I/O so pulling the brake
        //    takes effect on the very next tick even while the budget API is down.
        var switchReason = ResolveStopSwitch(context)?.GetStopReason();
        if (switchReason is not null)
        {
            await Stop(context, switchReason, 0);
            return;
        }

        // 2. Per-instance emergency stop (orchestrator config `limits.emergencyStop`).
        if (emergencyStop)
        {
            await Stop(context, "Emergency stop", 0);
            return;
        }

        // 3. Check active instances
        var activeCount = await GetActiveInstanceCount(context);
        ActiveInstances.Set(context, activeCount);

        if (activeCount >= maxConcurrent)
        {
            await Stop(context, $"Max concurrent reached ({activeCount}/{maxConcurrent})", activeCount);
            return;
        }

        // 4. Spend ceiling. `OperationalLimits.DailyBudgetUsd` shipped in AdlConfig from
        //    the start and nothing ever read it, so the loop had no cap of any kind;
        //    this is that check, wired to the API's real running spend.
        var spend = await EvaluateSpendAsync(context);
        SpentUsd.Set(context, spend.Spent);
        context.TransientProperties[EnforceableKey] = spend.Enforceable;
        if (spend.Decision.Stop)
        {
            await Stop(context, spend.Decision.Reason, activeCount);
            return;
        }

        // All checks passed.
        // 2026-08-13 (found by the engine-driven E2E): a literal `null` here
        // binds to Elsa's Set(Output<T>, ctx, Variable<T>) overload, whose null
        // Variable dereference throws NRE — so the HAPPY path of this activity
        // ALWAYS faulted and the orchestrator could never reach DispatchCycle.
        // The typed empty string keeps the "empty if continuing" output
        // contract while binding to the value overload.
        StopReason.Set(context, string.Empty);
        Logger?.LogInformation(
            "Limits OK: {Active}/{Max} active instances, spend ${Spent:F2}",
            activeCount, maxConcurrent, spend.Spent);
        await context.CompleteActivityWithOutcomesAsync("Continue");
    }

    /// <summary>
    /// Resolve the stop switch: injected → registered → constructed from configuration.
    /// The last hop matters because a store-rehydrated activity has null ctor members and
    /// the ElsaServer registration is the only DI wiring; a CLI/host without it must still
    /// honour the brake.
    /// </summary>
    private IAdlStopSwitch? ResolveStopSwitch(ActivityExecutionContext context)
    {
        if (_stopSwitch is not null) return _stopSwitch;
        var registered = context.GetService<IAdlStopSwitch>();
        if (registered is not null) return registered;
        var configuration = context.GetService<IConfiguration>();
        return configuration is null ? null : new ConfigAdlStopSwitch(configuration);
    }

    /// <summary>
    /// Read the period spend for the resolved budget owner and apply the ceiling. Returns
    /// the observed spend, whether the ceiling was actually ENFORCEABLE, and the decision
    /// — all three land in the audit event, because "the loop ran with no cap" has to be
    /// a queryable fact and not an assumption.
    /// </summary>
    private async Task<(decimal Spent, bool Enforceable, AdlSpendCeiling.Decision Decision)> EvaluateSpendAsync(
        ActivityExecutionContext context)
    {
        var configuration = context.GetService<IConfiguration>();

        var ceiling = MaxSpendUsd.GetOrDefault(context);
        if (ceiling <= 0m) ceiling = configuration?.GetValue<decimal?>(AdlSpendCeiling.MaxSpendKey) ?? 0m;

        var ownerId = FirstNonBlank(
            BudgetOwnerId.GetOrDefault(context),
            context.GetVariable<string>("TenantId"),
            context.GetVariable<string>("AccountId"),
            configuration?.GetValue<string?>(AdlSpendCeiling.BudgetOwnerKey));

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            if (ceiling > 0m)
            {
                Logger?.LogWarning(
                    "ADL spend ceiling of ${Ceiling:F2} is UNENFORCEABLE — no budget owner resolved. "
                    + "Set {Key} to a GUID (or run the loop under a tenant) to cap what the loop spends.",
                    ceiling, AdlSpendCeiling.BudgetOwnerKey);
            }
            return (0m, false, AdlSpendCeiling.EvaluateNoBudgetOwner());
        }

        var apiClient = context.GetService<TammaApiClient>();
        if (apiClient is null)
        {
            return (0m, false, AdlSpendCeiling.EvaluateUnknown(ceiling > 0m, "no TammaApiClient in this scope"));
        }

        try
        {
            var budget = await apiClient
                .GetBudgetAsync(ownerId, ownerId, context.CancellationToken)
                .ConfigureAwait(false);

            if (budget is null)
            {
                return (0m, false, AdlSpendCeiling.EvaluateUnknown(ceiling > 0m, "budget endpoint returned no status"));
            }

            var decision = AdlSpendCeiling.Evaluate(budget.Spent, budget.Limit, ceiling);
            return (budget.Spent, ceiling > 0m || budget.Limit > 0m, decision);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "ADL spend check failed for the configured budget owner");
            return (0m, false, AdlSpendCeiling.EvaluateUnknown(ceiling > 0m, $"budget read failed: {ex.GetType().Name}"));
        }
    }

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private async Task<int> GetActiveInstanceCount(ActivityExecutionContext context)
    {
        // 2026-08-14: a store-rehydrated activity has NULL ctor-injected members
        // (the same defect fixed in six sibling activities), so this returned 0
        // for EVERY tick — MaxConcurrent was never enforced and the ADL loop
        // could dispatch cycles without bound. The warning below could not even
        // report it, because the injected Logger is null for the same reason.
        var store = _workflowInstanceStore ?? context.GetService<IWorkflowInstanceStore>();
        var logger = Logger ?? context.GetService<ILogger<CheckLimitsActivity>>();
        if (store == null)
        {
            logger?.LogWarning("No IWorkflowInstanceStore available, assuming 0 active instances");
            return 0;
        }

        try
        {
            var filter = new WorkflowInstanceFilter
            {
                DefinitionId = "single-issue-cycle",
                WorkflowStatus = WorkflowStatus.Running,
            };

            var count = await store.CountAsync(filter);
            return (int)count;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to query active workflow instances");
            return 0; // fail open — don't block on query failure
        }
    }

    private async Task Stop(ActivityExecutionContext context, string reason, int active)
    {
        StopReason.Set(context, reason);
        ActiveInstances.Set(context, active);
        Logger?.LogWarning("Limits reached: {Reason}", reason);
        await context.CompleteActivityWithOutcomesAsync("Stop");
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["maxConcurrent"] = MaxConcurrent.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["activeInstances"] = this.GetOutput<int>(context, nameof(ActiveInstances)),
        ["stopReason"] = this.GetOutput<string?>(context, nameof(StopReason)),
        ["spentUsd"] = this.GetOutput<decimal>(context, nameof(SpentUsd)),
        ["ceilingEnforceable"] =
            context.TransientProperties.TryGetValue(EnforceableKey, out var e) && e is true,
    };
}
