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
        var budgetJson = BudgetStateJson.Get(context);
        var providerName = ProviderName.Get(context);

        var budget = DeserializeBudget(budgetJson);

        if (budget.CapUsd <= 0)
        {
            // No budget cap — always within budget
            _logger?.LogDebug("No budget cap configured, proceeding with {Provider}", providerName);
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
            "Budget check passed for {Provider}: ${Remaining:F4} remaining of ${Cap:F4}",
            providerName, budget.RemainingUsd, budget.CapUsd);

        await context.CompleteActivityWithOutcomesAsync("WithinBudget");
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
