using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Records diagnostics, updates circuit breaker and budget state inline.
/// Reads "LastDiagnostic" and "LastResponse" from workflow variables.
/// Used inside the Sequence-based retry loop of LlmCallWorkflow.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Record Diagnostics Inline",
    "Record attempt diagnostics and update circuit breaker/budget state inline",
    Kind = ActivityKind.Task
)]
public class RecordDiagnosticsInlineActivity : CodeActivity
{
    [Input(Description = "Provider name")]
    public Input<string> ProviderNameProp { get; set; } = default!;

    [Input(Description = "Accumulated diagnostics list JSON")]
    public Input<string> DiagnosticsListJsonProp { get; set; } = default!;

    [Input(Description = "Circuit breaker states JSON")]
    public Input<string> CircuitBreakerStatesJsonProp { get; set; } = default!;

    [Input(Description = "Budget state JSON")]
    public Input<string> BudgetStateJsonProp { get; set; } = default!;

    private readonly IErrorRedactor? _errorRedactor;

    [JsonConstructor]
    public RecordDiagnosticsInlineActivity() : this(null)
    {
    }

    public RecordDiagnosticsInlineActivity(IErrorRedactor? errorRedactor)
    {
        _errorRedactor = errorRedactor;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var providerName = ProviderNameProp.Get(context);
        var diagnosticsListJson = DiagnosticsListJsonProp.Get(context);
        var cbStatesJson = CircuitBreakerStatesJsonProp.Get(context);
        var budgetJson = BudgetStateJsonProp.Get(context);

        var diagJson = context.GetVariable<string>("LastDiagnostic") ?? "{}";

        ProviderAttemptDiagnostic? diagnostic;
        try { diagnostic = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(diagJson); }
        catch { diagnostic = new ProviderAttemptDiagnostic { ProviderName = providerName }; }
        diagnostic ??= new ProviderAttemptDiagnostic { ProviderName = providerName };

        // Redact sensitive information from error messages before storage
        if (_errorRedactor != null && !string.IsNullOrEmpty(diagnostic.ErrorMessage))
        {
            diagnostic.ErrorMessage = _errorRedactor.Redact(diagnostic.ErrorMessage);
        }

        // 1. Append diagnostic
        List<ProviderAttemptDiagnostic> list;
        try { list = JsonSerializer.Deserialize<List<ProviderAttemptDiagnostic>>(diagnosticsListJson ?? "[]") ?? new(); }
        catch { list = new(); }
        list.Add(diagnostic);

        // 2. Update circuit breaker
        Dictionary<string, CircuitBreakerState> cbStates;
        try { cbStates = JsonSerializer.Deserialize<Dictionary<string, CircuitBreakerState>>(cbStatesJson ?? "{}") ?? new(); }
        catch { cbStates = new(); }

        if (diagnostic.Succeeded)
        {
            cbStates = CheckCircuitBreakerActivity.RecordSuccess(cbStates, providerName);
        }
        else
        {
            cbStates = CheckCircuitBreakerActivity.RecordFailure(cbStates, providerName);
        }

        // 3. Update budget (simple cost estimate)
        BudgetState? budget;
        try { budget = JsonSerializer.Deserialize<BudgetState>(budgetJson ?? "{}"); }
        catch { budget = new BudgetState(); }
        budget ??= new BudgetState();

        if (diagnostic.PromptTokens > 0 || diagnostic.CompletionTokens > 0)
        {
            // Rough default cost estimate
            var costPer1KPrompt = 0.003m;
            var costPer1KCompletion = 0.015m;
            var cost = (diagnostic.PromptTokens / 1000m * costPer1KPrompt) +
                       (diagnostic.CompletionTokens / 1000m * costPer1KCompletion);
            budget.SpentUsd += cost;
        }

        // Write all state back
        var opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        context.SetVariable("DiagnosticsListJson", JsonSerializer.Serialize(list, opts));
        context.SetVariable("CircuitBreakerStatesJson", JsonSerializer.Serialize(cbStates, opts));
        context.SetVariable("BudgetStateJson", JsonSerializer.Serialize(budget, opts));
    }
}
