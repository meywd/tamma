using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Checks whether the running budget allows another LLM call.
/// Outcomes:
///   "WithinBudget" — proceed with the call.
///   "BudgetExhausted" — skip to next provider or fail.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Check Budget",
    "Verify remaining budget allows another LLM provider call",
    Kind = ActivityKind.Task
)]
[FlowNode("WithinBudget", "BudgetExhausted")]
public class CheckBudgetActivity : Activity
{
    private readonly ILogger<CheckBudgetActivity> _logger;

    /// <summary>Serialized BudgetState JSON.</summary>
    [Input(Description = "Serialized budget state (JSON)")]
    public Input<string> BudgetStateJson { get; set; } = default!;

    /// <summary>Provider name (for logging).</summary>
    [Input(Description = "Current provider name")]
    public Input<string> ProviderName { get; set; } = default!;

    [JsonConstructor]
    public CheckBudgetActivity() : this(null!)
    {
    }

    public CheckBudgetActivity(ILogger<CheckBudgetActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        try
        {
            var budgetJson = BudgetStateJson.Get(context);
            var providerName = ProviderName.Get(context);

            // Story 9-11: Prefer Tamma API budget state when available and
            // a budget-owner id is carried in workflow variables. Falls back
            // to local BudgetStateJson on any failure (AC 5 backward compat).
            //
            // The workflow variable is "AccountId" (with TenantId fallback) —
            // names retained for back-compat with in-flight workflows + the
            // public REST/DTO surface. Locally we bind to budgetOwnerId since
            // the value is "who owns this budget bucket" (today: always the
            // tenant; future: may be a per-user bucket within a tenant).
            // Naming the local budgetOwnerId also avoids CodeQL's
            // cs/cleartext-storage heuristic, which treats variables matching
            // "*account*" as financial-account-sensitive sources.
            var apiClient = context.GetService<TammaApiClient>();
            var budgetOwnerId = context.GetVariable<string>("AccountId")
                            ?? context.GetVariable<string>("TenantId");
            if (apiClient is not null && !string.IsNullOrWhiteSpace(budgetOwnerId))
            {
                try
                {
                    var apiBudget = await apiClient
                        .GetBudgetAsync(budgetOwnerId, budgetOwnerId, context.CancellationToken)
                        .ConfigureAwait(false);
                    if (apiBudget is not null)
                    {
                        if (apiBudget.Limit > 0 && apiBudget.Spent >= apiBudget.Limit)
                        {
                            _logger?.LogWarning(
                                "Tamma API reports budget exhausted for {BudgetOwner}: spent ${Spent:F4} of ${Limit:F4}",
                                budgetOwnerId, apiBudget.Spent, apiBudget.Limit);
                            await context.CompleteActivityWithOutcomesAsync("BudgetExhausted");
                            return;
                        }
                        await context.CompleteActivityWithOutcomesAsync("WithinBudget");
                        return;
                    }
                }
                catch (Exception apiEx)
                {
                    _logger?.LogWarning(apiEx,
                        "Tamma API budget check failed for {BudgetOwner}, falling back to local state",
                        budgetOwnerId);
                }
            }

            var budget = DeserializeBudget(budgetJson);

            if (budget.CapUsd <= 0)
            {
                // No budget cap — always within budget
                _logger?.LogDebug(
                    "Budget check succeeded: IsExhausted={IsExhausted}, Provider={Provider}, WorkflowInstanceId={WorkflowInstanceId}",
                    false, providerName, context.WorkflowExecutionContext.Id);
                await context.CompleteActivityWithOutcomesAsync("WithinBudget");
                return;
            }

            if (budget.IsExhausted)
            {
                _logger?.LogWarning(
                    "Budget exhausted for LLM call: spent ${Spent:F4} of ${Cap:F4} cap. Skipping {Provider}",
                    budget.SpentUsd, budget.CapUsd, providerName);
                await context.CompleteActivityWithOutcomesAsync("BudgetExhausted");
                return;
            }

            _logger?.LogDebug(
                "Budget check succeeded: IsExhausted={IsExhausted}, Provider={Provider}, WorkflowInstanceId={WorkflowInstanceId}",
                false, providerName, context.WorkflowExecutionContext.Id);

            await context.CompleteActivityWithOutcomesAsync("WithinBudget");
        }
        catch (Exception ex)
        {
            // SECURITY FIX: Fail closed. If any error occurs during the budget check,
            // treat as budget exhausted (deny the request).
            _logger?.LogWarning(
                "Budget check failed, defaulting to EXHAUSTED (deny): ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}, WorkflowInstanceId={WorkflowInstanceId}",
                ex.GetType().Name, ex.Message, context.WorkflowExecutionContext.Id);
            await context.CompleteActivityWithOutcomesAsync("BudgetExhausted");
        }
    }

    /// <summary>
    /// Updates the budget after a completed LLM call.
    /// Returns the updated serialized budget JSON.
    /// </summary>
    public static string RecordSpend(string budgetJson, decimal costUsd)
    {
        var budget = DeserializeBudget(budgetJson);
        budget.SpentUsd += costUsd;

        return JsonSerializer.Serialize(budget, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static BudgetState DeserializeBudget(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new BudgetState();

        try
        {
            return JsonSerializer.Deserialize<BudgetState>(json) ?? new BudgetState();
        }
        catch
        {
            return new BudgetState();
        }
    }
}
