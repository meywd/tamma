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
/// Checks the circuit breaker state for the current provider.
/// Outcomes:
///   "Closed"   — provider is healthy, proceed with the call.
///   "HalfOpen" — cooldown elapsed, allow one probe request.
///   "Open"     — provider is tripped, skip to next provider.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Check Circuit Breaker",
    "Evaluate circuit breaker state for the current LLM provider",
    Kind = ActivityKind.Task
)]
[FlowNode("Closed", "HalfOpen", "Open")]
public class CheckCircuitBreakerActivity : Activity
{
    private readonly ILogger<CheckCircuitBreakerActivity> _logger;

    /// <summary>Provider key to check (e.g. "anthropic").</summary>
    [Input(Description = "Provider key to check")]
    public Input<string> ProviderName { get; set; } = default!;

    /// <summary>
    /// Serialized circuit breaker state dictionary (JSON string).
    /// Key = provider name, Value = CircuitBreakerState.
    /// </summary>
    [Input(Description = "Serialized circuit breaker states (JSON dictionary)")]
    public Input<string> CircuitBreakerStatesJson { get; set; } = default!;

    [JsonConstructor]
    public CheckCircuitBreakerActivity() : this(null!)
    {
    }

    public CheckCircuitBreakerActivity(ILogger<CheckCircuitBreakerActivity> logger)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        try
        {
            var providerName = ProviderName.Get(context);
            var statesJson = CircuitBreakerStatesJson.Get(context);

            var states = DeserializeStates(statesJson);

            if (!states.TryGetValue(providerName, out var state))
            {
                // No state tracked yet — treat as Closed
                _logger?.LogDebug(
                    "Circuit breaker check succeeded: IsOpen={IsOpen}, Provider={Provider}, WorkflowInstanceId={WorkflowInstanceId}",
                    false, providerName, context.WorkflowExecutionContext.Id);
                await context.CompleteActivityWithOutcomesAsync("Closed");
                return;
            }

            switch (state.Status)
            {
                case CircuitBreakerStatus.Closed:
                    _logger?.LogDebug(
                        "Circuit breaker check succeeded: IsOpen={IsOpen}, Provider={Provider}, WorkflowInstanceId={WorkflowInstanceId}",
                        false, providerName, context.WorkflowExecutionContext.Id);
                    await context.CompleteActivityWithOutcomesAsync("Closed");
                    break;

                case CircuitBreakerStatus.Open:
                    // Check if cooldown has elapsed
                    if (state.OpenedAtUtc.HasValue &&
                        DateTime.UtcNow - state.OpenedAtUtc.Value >= state.CooldownPeriod)
                    {
                        _logger?.LogInformation(
                            "Circuit breaker for {Provider} cooldown elapsed, transitioning to HalfOpen",
                            providerName);

                        state.Status = CircuitBreakerStatus.HalfOpen;
                        states[providerName] = state;

                        // Write updated state back
                        context.SetVariable("CircuitBreakerStatesJson", SerializeStates(states));
                        await context.CompleteActivityWithOutcomesAsync("HalfOpen");
                    }
                    else
                    {
                        var remaining = state.OpenedAtUtc.HasValue
                            ? state.CooldownPeriod - (DateTime.UtcNow - state.OpenedAtUtc.Value)
                            : state.CooldownPeriod;

                        _logger?.LogWarning(
                            "Circuit breaker for {Provider} is Open, {Remaining}s remaining",
                            providerName, remaining.TotalSeconds);

                        await context.CompleteActivityWithOutcomesAsync("Open");
                    }
                    break;

                case CircuitBreakerStatus.HalfOpen:
                    _logger?.LogInformation(
                        "Circuit breaker for {Provider} is HalfOpen, allowing probe request",
                        providerName);
                    await context.CompleteActivityWithOutcomesAsync("HalfOpen");
                    break;

                default:
                    await context.CompleteActivityWithOutcomesAsync("Closed");
                    break;
            }
        }
        catch (Exception ex)
        {
            // SECURITY FIX: Fail closed. If any error occurs during the circuit breaker check,
            // treat the circuit as Open (deny the request).
            _logger?.LogWarning(
                "Circuit breaker check failed, defaulting to OPEN (deny): ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}, WorkflowInstanceId={WorkflowInstanceId}",
                ex.GetType().Name, ex.Message, context.WorkflowExecutionContext.Id);
            await context.CompleteActivityWithOutcomesAsync("Open");
        }
    }

    /// <summary>
    /// Records a success for the given provider, resetting the breaker to Closed.
    /// Call this static helper from RecordDiagnosticsActivity after a successful call.
    /// </summary>
    public static Dictionary<string, CircuitBreakerState> RecordSuccess(
        Dictionary<string, CircuitBreakerState> states,
        string providerName)
    {
        if (states.TryGetValue(providerName, out var state))
        {
            state.Status = CircuitBreakerStatus.Closed;
            state.ConsecutiveFailures = 0;
            state.LastSuccessAtUtc = DateTime.UtcNow;
            state.OpenedAtUtc = null;
        }

        return states;
    }

    /// <summary>
    /// Records a failure for the given provider, potentially tripping the breaker.
    /// Call this static helper from RecordDiagnosticsActivity after a failed call.
    /// </summary>
    public static Dictionary<string, CircuitBreakerState> RecordFailure(
        Dictionary<string, CircuitBreakerState> states,
        string providerName,
        int failureThreshold = 5,
        int cooldownSeconds = 300)
    {
        if (!states.TryGetValue(providerName, out var state))
        {
            state = new CircuitBreakerState
            {
                ProviderName = providerName,
                FailureThreshold = failureThreshold,
                CooldownPeriod = TimeSpan.FromSeconds(cooldownSeconds)
            };
            states[providerName] = state;
        }

        state.ConsecutiveFailures++;
        state.LastFailureAtUtc = DateTime.UtcNow;

        if (state.ConsecutiveFailures >= state.FailureThreshold)
        {
            state.Status = CircuitBreakerStatus.Open;
            state.OpenedAtUtc = DateTime.UtcNow;
        }

        return states;
    }

    private static Dictionary<string, CircuitBreakerState> DeserializeStates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, CircuitBreakerState>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CircuitBreakerState>>(json)
                   ?? new Dictionary<string, CircuitBreakerState>();
        }
        catch
        {
            return new Dictionary<string, CircuitBreakerState>();
        }
    }

    internal static string SerializeStates(Dictionary<string, CircuitBreakerState> states)
    {
        return JsonSerializer.Serialize(states, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}
