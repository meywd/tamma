using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

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
    private readonly IErrorRedactor? _errorRedactor;

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
    public RecordDiagnosticsActivity() : this(null!, null!, null)
    {
    }

    public RecordDiagnosticsActivity(
        ILogger<RecordDiagnosticsActivity> logger,
        IConfiguration configuration,
        IErrorRedactor? errorRedactor)
    {
        _logger = logger;
        _configuration = configuration;
        _errorRedactor = errorRedactor;
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

        // Redact sensitive information from error messages before storage
        if (_errorRedactor != null && !string.IsNullOrEmpty(diagnostic.ErrorMessage))
        {
            diagnostic.ErrorMessage = _errorRedactor.Redact(diagnostic.ErrorMessage);
        }
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

        // Story 9-11: Additionally write the diagnostic to the Tamma API so
        // the persistent store + shared cost tracker see the event. Local
        // workflow-variable state is kept intact for backward compat.
        var apiClient = context.GetService<TammaApiClient>();
        if (apiClient is not null)
        {
            try
            {
                var accountId = context.GetVariable<string>("AccountId")
                                ?? context.GetVariable<string>("TenantId");
                var correlationId = context.GetVariable<string>("CorrelationId");
                var cost = EstimateCost(providerName, diagnostic.PromptTokens, diagnostic.CompletionTokens);
                var role = context.GetVariable<string>("Role");
                var action = context.GetVariable<string>("OperationName");

                await apiClient.RecordDiagnosticsAsync(
                    new Models.DiagnosticsIngestRequest(
                        Provider: providerName,
                        Model: response.Model,
                        Role: role,
                        Action: action,
                        Success: diagnostic.Succeeded,
                        PromptTokens: diagnostic.PromptTokens,
                        CompletionTokens: diagnostic.CompletionTokens,
                        TotalTokens: diagnostic.PromptTokens + diagnostic.CompletionTokens,
                        CostUsd: cost,
                        DurationMs: diagnostic.DurationMs,
                        ErrorMessage: diagnostic.ErrorMessage,
                        AccountId: accountId,
                        CorrelationId: correlationId),
                    tenantId: accountId,
                    ct: context.CancellationToken).ConfigureAwait(false);

                if (diagnostic.Succeeded)
                {
                    await apiClient.RecordProviderSuccessAsync(
                        providerName, accountId, context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await apiClient.RecordProviderFailureAsync(
                        providerName, diagnostic.ErrorMessage, accountId, context.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception apiEx)
            {
                // Non-fatal — local state already updated.
                _logger?.LogWarning(apiEx,
                    "Tamma API diagnostics record failed (local state retained) for {Provider}",
                    providerName);
            }
        }

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
