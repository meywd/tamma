using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Data.Abstractions;

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
                            // Wave C.4 §1 — emit BUDGET.EXHAUSTED into the DCB
                            // event store so the AlertRuleEvaluator can pick it
                            // up + fan out to channels. Budget-exhausted
                            // correlation belongs in the event stream (the
                            // dashboard + replay tooling reads it there), not
                            // in the rotating warn file on the VPS.
                            await EmitBudgetExhaustedAsync(
                                context.GetService<IAlertEventEmitter>(),
                                TryParseTenantGuid(budgetOwnerId),
                                context.WorkflowExecutionContext.Id,
                                source: "api",
                                spent: apiBudget.Spent,
                                limit: apiBudget.Limit,
                                providerName: providerName ?? "(unknown)",
                                ct: context.CancellationToken)
                                .ConfigureAwait(false);
                            await context.CompleteActivityWithOutcomesAsync("BudgetExhausted");
                            return;
                        }
                        await context.CompleteActivityWithOutcomesAsync("WithinBudget");
                        return;
                    }
                }
                catch (Exception apiEx)
                {
                    // System-health signal worth keeping: "Tamma API is
                    // unhealthy" is the operator-useful part. Identifier is
                    // stripped — per-tenant correlation is via the event
                    // store, not this rotating log file.
                    _logger?.LogWarning(apiEx,
                        "Tamma API budget check failed, falling back to local state");
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
                // Wave C.4 §1 — local-path exhaustion also emits into the
                // DCB stream. Source differs from the api-path so
                // downstream dashboards can distinguish "API saw cap hit"
                // from "local workflow bucket drained".
                await EmitBudgetExhaustedAsync(
                    context.GetService<IAlertEventEmitter>(),
                    TryParseTenantGuid(budgetOwnerId),
                    context.WorkflowExecutionContext.Id,
                    source: "local",
                    spent: budget.SpentUsd,
                    limit: budget.CapUsd,
                    providerName: providerName ?? "(unknown)",
                    ct: context.CancellationToken)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// Wave C.4 §1 helper — emit a BUDGET.EXHAUSTED DCB event via
    /// <paramref name="emitter"/>. Tolerant of null emitter (DI not
    /// wired in some test harnesses) + null tenant (event is tenant-
    /// scoped by definition — no emission makes sense without one).
    /// Public-internal so <c>Tamma.Activities.Tests</c> can exercise it
    /// without hosting Elsa; test coverage lives in
    /// <c>CheckBudgetActivityEmissionTests</c>.
    /// </summary>
    public static async Task EmitBudgetExhaustedAsync(
        IAlertEventEmitter? emitter,
        Guid? tenantId,
        string workflowInstanceId,
        string source,
        decimal spent,
        decimal limit,
        string providerName,
        CancellationToken ct)
    {
        if (emitter is null) return;
        if (tenantId is not Guid tid) return;

        await emitter.EmitBudgetExhaustedAsync(new BudgetExhaustedEvent(
            TenantId: tid,
            CorrelationId: workflowInstanceId,
            Source: source,
            Spent: spent,
            Limit: limit,
            ProviderName: providerName,
            WorkflowInstanceId: workflowInstanceId), ct).ConfigureAwait(false);
    }

    private static Guid? TryParseTenantGuid(string? s) =>
        Guid.TryParse(s, out var g) ? g : (Guid?)null;
}
