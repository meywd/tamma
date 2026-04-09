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

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Checks whether the number of currently running LLM call workflow instances
/// is below the configured concurrency limit.
///
/// Used in the LlmCallWorkflow to implement a wait-loop:
///   While(ConcurrencyAtLimit) { CheckConcurrency → Delay }
///
/// Sets workflow variables:
///   - ConcurrencyAtLimit (bool): true if at or over the limit
///   - ConcurrencyActiveCount (int): current count of running instances
///
/// When used as a standalone flowchart activity:
///   Outcomes: "OK" (under limit), "AtLimit" (at/over limit)
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Check LLM Concurrency",
    "Check whether concurrent LLM call count is within limits",
    Kind = ActivityKind.Task
)]
[FlowNode("OK", "AtLimit")]
public class CheckLlmConcurrencyActivity : TammaOutcomeActivity
{
    public override string? EventType => "LLM.CONCURRENCY.CHECK";

    private readonly IWorkflowInstanceStore? _workflowInstanceStore;
    private readonly IConfiguration? _configuration;

    // --- Inputs ---

    [Input(Description = "Max concurrent LLM call workflow instances (0 = use config or default 5)")]
    public Input<int> MaxConcurrentLlmCalls { get; set; } = new(0);

    // --- Outputs ---

    [Output(Description = "Number of currently running LLM call instances")]
    public Output<int> ActiveInstances { get; set; } = default!;

    [Output(Description = "True if at or over the concurrency limit")]
    public Output<bool> AtLimit { get; set; } = default!;

    [JsonConstructor]
    public CheckLlmConcurrencyActivity() { }

    public CheckLlmConcurrencyActivity(
        ILogger<CheckLlmConcurrencyActivity> logger,
        IWorkflowInstanceStore workflowInstanceStore,
        IConfiguration configuration)
    {
        Logger = logger;
        _workflowInstanceStore = workflowInstanceStore;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        // Resolve the limit: Input value > IConfiguration > default (5)
        var maxConcurrent = MaxConcurrentLlmCalls.Get(context);
        if (maxConcurrent <= 0)
        {
            var configValue = _configuration?.GetValue<int?>("Tamma:MaxConcurrentLlmCalls");
            maxConcurrent = configValue ?? 5;
        }

        // No limit configured
        if (maxConcurrent <= 0)
        {
            ActiveInstances.Set(context, 0);
            AtLimit.Set(context, false);
            Logger?.LogDebug("LLM concurrency check: no limit configured, proceeding");
            await context.CompleteActivityWithOutcomesAsync("OK");
            return;
        }

        // Query running instances
        var activeCount = await GetActiveInstanceCount();
        ActiveInstances.Set(context, activeCount);

        if (activeCount >= maxConcurrent)
        {
            AtLimit.Set(context, true);
            Logger?.LogInformation(
                "LLM concurrency limit reached: {Active}/{Max} active instances, waiting for slot",
                activeCount, maxConcurrent);
            await context.CompleteActivityWithOutcomesAsync("AtLimit");
            return;
        }

        AtLimit.Set(context, false);
        Logger?.LogDebug(
            "LLM concurrency OK: {Active}/{Max} active instances",
            activeCount, maxConcurrent);
        await context.CompleteActivityWithOutcomesAsync("OK");
    }

    private async Task<int> GetActiveInstanceCount()
    {
        if (_workflowInstanceStore == null)
        {
            Logger?.LogWarning("No IWorkflowInstanceStore available, assuming 0 active LLM call instances");
            return 0;
        }

        try
        {
            var filter = new WorkflowInstanceFilter
            {
                DefinitionId = "llm-call",
                WorkflowStatus = WorkflowStatus.Running,
            };

            var count = await _workflowInstanceStore.CountAsync(filter);
            return (int)count;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to query active LLM call workflow instances");
            return 0; // fail open — don't block on query failure
        }
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["maxConcurrentLlmCalls"] = MaxConcurrentLlmCalls.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["activeInstances"] = this.GetOutput<int>(context, nameof(ActiveInstances)),
        ["atLimit"] = this.GetOutput<bool>(context, nameof(AtLimit)),
    };
}
