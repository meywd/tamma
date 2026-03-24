using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Records diagnostics after each LLM call attempt.
/// Updates:
///   - The diagnostics list (all attempts)
///   - Circuit breaker state (success resets, failure increments)
///   - Budget state (adds estimated cost)
///   - The composite workflow output (on success)
///
/// This activity always completes with "Done"; the workflow decides
/// whether to continue, retry, or fail based on the diagnostic.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Record Diagnostics",
    "Record attempt diagnostics, update circuit breaker and budget state",
    Kind = ActivityKind.Task
)]
public class RecordDiagnosticsActivity : CodeActivity<string>
{
    private readonly ILogger<RecordDiagnosticsActivity> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>Serialized ProviderAttemptDiagnostic JSON from CallLlmActivity.</summary>
    [Input(Description = "Serialized diagnostic from the last call attempt")]
    public Input<string> LastDiagnosticJson { get; set; } = default!;

    /// <summary>Serialized NormalizedLlmResponse JSON from CallLlmActivity.</summary>
    [Input(Description = "Serialized LLM response from the last call attempt")]
    public Input<string> LastResponseJson { get; set; } = default!;

    /// <summary>Serialized diagnostics list (JSON array of ProviderAttemptDiagnostic).</summary>
    [Input(Description = "Accumulated diagnostics list (JSON array)")]
    public Input<string> DiagnosticsListJson { get; set; } = default!;

    /// <summary>Serialized circuit breaker states (JSON dictionary).</summary>
    [Input(Description = "Serialized circuit breaker states")]
    public Input<string> CircuitBreakerStatesJson { get; set; } = default!;

    /// <summary>Serialized budget state (JSON).</summary>
    [Input(Description = "Serialized budget state")]
    public Input<string> BudgetStateJson { get; set; } = default!;

    /// <summary>Provider name for this attempt.</summary>
    [Input(Description = "Provider name")]
    public Input<string> ProviderName { get; set; } = default!;

    [JsonConstructor]
    public RecordDiagnosticsActivity() : this(null!, null!)
    {
    }

    public RecordDiagnosticsActivity(
        ILogger<RecordDiagnosticsActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var diagnosticJson = LastDiagnosticJson.Get(context);
        var responseJson = LastResponseJson.Get(context);
        var diagnosticsListJson = DiagnosticsListJson.Get(context);
        var cbStatesJson = CircuitBreakerStatesJson.Get(context);
        var budgetJson = BudgetStateJson.Get(context);
        var providerName = ProviderName.Get(context);

        // Deserialize inputs
        var diagnostic = Deserialize<ProviderAttemptDiagnostic>(diagnosticJson) ?? new ProviderAttemptDiagnostic();
        var response = Deserialize<NormalizedLlmResponse>(responseJson) ?? new NormalizedLlmResponse();
        var diagnosticsList = Deserialize<List<ProviderAttemptDiagnostic>>(diagnosticsListJson) ?? new();
        var cbStates = Deserialize<Dictionary<string, CircuitBreakerState>>(cbStatesJson) ?? new();
        var budget = Deserialize<BudgetState>(budgetJson) ?? new BudgetState();

        // 1. Append diagnostic to the list
        diagnosticsList.Add(diagnostic);

        // 2. Update circuit breaker
        if (diagnostic.Succeeded)
        {
            cbStates = CheckCircuitBreakerActivity.RecordSuccess(cbStates, providerName);
        }
        else
        {
            var config = LoadProviderConfig(providerName);
            cbStates = CheckCircuitBreakerActivity.RecordFailure(
                cbStates, providerName,
                config.CircuitBreakerFailureThreshold,
                config.CircuitBreakerCooldownSeconds);
        }

        // 3. Update budget
        if (diagnostic.Succeeded || diagnostic.PromptTokens > 0)
        {
            var cost = EstimateCost(providerName, diagnostic.PromptTokens, diagnostic.CompletionTokens);
            budget.SpentUsd += cost;

            _logger?.LogDebug(
                "Cost for {Provider} attempt: ${Cost:F6}, total spent: ${Total:F6}",
                providerName, cost, budget.SpentUsd);
        }

        // 4. Write all updated state back to workflow variables
        context.SetVariable("DiagnosticsListJson", Serialize(diagnosticsList));
        context.SetVariable("CircuitBreakerStatesJson", CheckCircuitBreakerActivity.SerializeStates(cbStates));
        context.SetVariable("BudgetStateJson", Serialize(budget));

        // 5. If successful, build the composite output
        if (diagnostic.Succeeded)
        {
            var output = new LlmCallWorkflowOutput
            {
                Success = true,
                ResponseText = response.ResponseText,
                SuccessfulProvider = providerName,
                ModelUsed = response.Model,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                TotalTokens = response.PromptTokens + response.CompletionTokens,
                EstimatedCostUsd = budget.SpentUsd,
                TotalDurationMs = diagnosticsList.Sum(d => d.DurationMs),
                Diagnostics = diagnosticsList,
                ToolCalls = response.ToolCalls
            };

            context.SetVariable("WorkflowOutputJson", Serialize(output));
        }

        _logger?.LogInformation(
            "Diagnostics recorded: provider={Provider}, attempt={Attempt}, succeeded={Succeeded}, cost=${Cost:F6}",
            providerName, diagnostic.AttemptNumber, diagnostic.Succeeded, budget.SpentUsd);

        // Return the updated diagnostics list JSON as the activity result
        context.SetResult(Serialize(diagnosticsList));
    }

    private decimal EstimateCost(string providerName, int promptTokens, int completionTokens)
    {
        var section = _configuration?.GetSection($"LlmProviders:{providerName}");

        decimal costPer1KPrompt = 0;
        decimal costPer1KCompletion = 0;

        if (section != null && section.Exists())
        {
            if (decimal.TryParse(section["CostPer1KPromptTokens"], out var cp))
                costPer1KPrompt = cp;
            if (decimal.TryParse(section["CostPer1KCompletionTokens"], out var cc))
                costPer1KCompletion = cc;
        }
        else
        {
            // Well-known defaults (approximate)
            (costPer1KPrompt, costPer1KCompletion) = providerName.ToLowerInvariant() switch
            {
                "anthropic" => (0.003m, 0.015m),
                "openai" => (0.005m, 0.015m),
                "openrouter" => (0.003m, 0.015m),
                _ => (0.001m, 0.002m)
            };
        }

        return (promptTokens / 1000m * costPer1KPrompt) +
               (completionTokens / 1000m * costPer1KCompletion);
    }

    private LlmProviderConfig LoadProviderConfig(string providerName)
    {
        var section = _configuration?.GetSection($"LlmProviders:{providerName}");
        var config = new LlmProviderConfig { Name = providerName };

        if (section == null || !section.Exists())
            return config;

        if (int.TryParse(section["CircuitBreakerFailureThreshold"], out var threshold))
            config.CircuitBreakerFailureThreshold = threshold;
        if (int.TryParse(section["CircuitBreakerCooldownSeconds"], out var cooldown))
            config.CircuitBreakerCooldownSeconds = cooldown;

        return config;
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string Serialize<T>(T obj)
    {
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}
