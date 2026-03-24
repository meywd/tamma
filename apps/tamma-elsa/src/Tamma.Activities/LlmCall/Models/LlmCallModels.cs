using System.Text.Json.Serialization;

namespace Tamma.Activities.LlmCall.Models;

// ============================================================
// Workflow Input / Output
// ============================================================

/// <summary>
/// Input parameters for the LLM Call sub-workflow.
/// Callers populate this and pass it as workflow input.
/// </summary>
public class LlmCallWorkflowInput
{
    /// <summary>Logical operation name for diagnostics and prompt resolution (e.g. "code_review", "blocker_diagnosis").</summary>
    public string OperationName { get; set; } = string.Empty;

    /// <summary>The role / persona the LLM should adopt (e.g. "mentor", "code_reviewer").</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>User-supplied prompt content (the "user message").</summary>
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>Optional system prompt override. When blank the 6-level resolver fills it.</summary>
    public string? SystemPromptOverride { get; set; }

    /// <summary>Ordered list of provider keys to try (e.g. ["anthropic", "openai", "openrouter"]).</summary>
    public List<string> ProviderChain { get; set; } = new();

    /// <summary>Model override per provider (key = provider name).</summary>
    public Dictionary<string, string> ModelOverrides { get; set; } = new();

    /// <summary>Maximum tokens for the completion.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Temperature (0.0 – 2.0).</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Optional list of tool/function names the LLM may invoke.</summary>
    public List<string>? ToolNames { get; set; }

    /// <summary>Budget cap in USD for this single call (0 = unlimited).</summary>
    public decimal BudgetCapUsd { get; set; }

    /// <summary>Correlation / trace ID for linking back to the parent workflow.</summary>
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Composite output returned by the LLM Call sub-workflow.
/// </summary>
public class LlmCallWorkflowOutput
{
    /// <summary>Whether the call succeeded on any provider.</summary>
    public bool Success { get; set; }

    /// <summary>The final LLM response text.</summary>
    public string? ResponseText { get; set; }

    /// <summary>Name of the provider that produced the successful response.</summary>
    public string? SuccessfulProvider { get; set; }

    /// <summary>Model actually used.</summary>
    public string? ModelUsed { get; set; }

    /// <summary>Prompt tokens consumed.</summary>
    public int PromptTokens { get; set; }

    /// <summary>Completion tokens consumed.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Total tokens consumed.</summary>
    public int TotalTokens { get; set; }

    /// <summary>Estimated cost in USD for the successful call.</summary>
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>Total wall-clock milliseconds across all attempts.</summary>
    public long TotalDurationMs { get; set; }

    /// <summary>Per-provider attempt diagnostics (includes failures).</summary>
    public List<ProviderAttemptDiagnostic> Diagnostics { get; set; } = new();

    /// <summary>Error message when Success == false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Tool calls returned by the LLM, if any.</summary>
    public List<LlmToolCall>? ToolCalls { get; set; }
}

// ============================================================
// Diagnostics
// ============================================================

/// <summary>
/// Diagnostic record for a single provider attempt.
/// </summary>
public class ProviderAttemptDiagnostic
{
    /// <summary>Provider key (e.g. "anthropic").</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Model used for this attempt.</summary>
    public string? Model { get; set; }

    /// <summary>1-based attempt number within this provider.</summary>
    public int AttemptNumber { get; set; }

    /// <summary>Whether this attempt succeeded.</summary>
    public bool Succeeded { get; set; }

    /// <summary>HTTP status code returned (0 if no HTTP call was made).</summary>
    public int HttpStatusCode { get; set; }

    /// <summary>Error message if the attempt failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Timestamp (UTC) when this attempt started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Prompt tokens for this attempt.</summary>
    public int PromptTokens { get; set; }

    /// <summary>Completion tokens for this attempt.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Whether the circuit breaker was open and this attempt was skipped.</summary>
    public bool CircuitBreakerSkipped { get; set; }

    /// <summary>Whether this attempt was skipped due to budget exhaustion.</summary>
    public bool BudgetExhausted { get; set; }
}

// ============================================================
// Circuit Breaker
// ============================================================

/// <summary>
/// Tracks circuit breaker state for a single provider.
/// Stored as a workflow variable (Dictionary&lt;string, CircuitBreakerState&gt;).
/// </summary>
public class CircuitBreakerState
{
    /// <summary>Provider key this state belongs to.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Current breaker status.</summary>
    public CircuitBreakerStatus Status { get; set; } = CircuitBreakerStatus.Closed;

    /// <summary>Consecutive failure count.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Threshold of consecutive failures before opening.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>When the breaker was opened (UTC). Null when Closed.</summary>
    public DateTime? OpenedAtUtc { get; set; }

    /// <summary>How long the breaker stays open before moving to HalfOpen.</summary>
    public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>Timestamp of the most recent failure (UTC).</summary>
    public DateTime? LastFailureAtUtc { get; set; }

    /// <summary>Timestamp of the most recent success (UTC).</summary>
    public DateTime? LastSuccessAtUtc { get; set; }
}

/// <summary>
/// Circuit breaker status values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CircuitBreakerStatus
{
    /// <summary>Normal — requests flow through.</summary>
    Closed,

    /// <summary>Tripped — requests are rejected immediately.</summary>
    Open,

    /// <summary>Cooldown elapsed — allow one probe request.</summary>
    HalfOpen
}

// ============================================================
// Prompt Resolution
// ============================================================

/// <summary>
/// Result of the 6-level prompt resolution hierarchy.
/// </summary>
public class ResolvedPrompt
{
    /// <summary>The final system prompt text.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>The user prompt text (passed through or enriched).</summary>
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>Which resolution level provided the system prompt.</summary>
    public PromptResolutionLevel ResolvedLevel { get; set; }

    /// <summary>Config key that was matched (for diagnostics).</summary>
    public string? MatchedConfigKey { get; set; }
}

/// <summary>
/// The 6-level prompt resolution hierarchy (highest priority first).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptResolutionLevel
{
    /// <summary>Level 1 — per-provider, per-role override (e.g. "LlmPrompts:anthropic:code_reviewer").</summary>
    PerProviderPerRole,

    /// <summary>Level 2 — per-provider default (e.g. "LlmPrompts:anthropic:default").</summary>
    PerProviderDefault,

    /// <summary>Level 3 — per-role override (e.g. "LlmPrompts:roles:code_reviewer").</summary>
    PerRole,

    /// <summary>Level 4 — per-operation override (e.g. "LlmPrompts:operations:blocker_diagnosis").</summary>
    PerOperation,

    /// <summary>Level 5 — global default from config (e.g. "LlmPrompts:default").</summary>
    GlobalDefault,

    /// <summary>Level 6 — hardcoded fallback baked into the activity.</summary>
    HardcodedFallback,

    /// <summary>Caller provided an explicit override — no resolution needed.</summary>
    CallerOverride
}

// ============================================================
// Tool / Function Calling
// ============================================================

/// <summary>
/// Describes a resolved tool definition to send to the LLM.
/// </summary>
public class ResolvedTool
{
    /// <summary>Tool / function name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Natural-language description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON Schema for the tool's input parameters (serialized as a dictionary).</summary>
    public Dictionary<string, object>? InputSchema { get; set; }
}

/// <summary>
/// A tool call returned by the LLM in its response.
/// </summary>
public class LlmToolCall
{
    /// <summary>Tool call ID assigned by the provider.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Name of the tool the LLM wants to invoke.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>JSON-serialized arguments for the tool.</summary>
    public string ArgumentsJson { get; set; } = "{}";
}

// ============================================================
// Budget Tracking
// ============================================================

/// <summary>
/// Running budget state for the current workflow invocation.
/// </summary>
public class BudgetState
{
    /// <summary>Configured cap in USD (0 = unlimited).</summary>
    public decimal CapUsd { get; set; }

    /// <summary>Amount spent so far in USD.</summary>
    public decimal SpentUsd { get; set; }

    /// <summary>Whether the budget has been exhausted.</summary>
    public bool IsExhausted => CapUsd > 0 && SpentUsd >= CapUsd;

    /// <summary>Remaining budget in USD (negative means over-budget).</summary>
    public decimal RemainingUsd => CapUsd > 0 ? CapUsd - SpentUsd : decimal.MaxValue;
}

// ============================================================
// Agent Custom Settings (stored in ExecutionSettings.ResponseFormat)
// ============================================================

/// <summary>
/// Custom settings stored as JSON in the ELSA Agent's ExecutionSettings.ResponseFormat field.
/// Contains Tamma-specific configuration that doesn't map to standard Semantic Kernel settings.
/// </summary>
public class AgentCustomSettings
{
    /// <summary>Ordered provider chain for this agent (e.g. ["anthropic", "openai", "openrouter"]).</summary>
    public List<string>? ProviderChain { get; set; }

    /// <summary>Maximum budget in USD for a single LLM call by this agent.</summary>
    public decimal MaxBudgetUsd { get; set; }
}

// ============================================================
// Provider Configuration (read from IConfiguration)
// ============================================================

/// <summary>
/// Configuration block for a single LLM provider.
/// Bound from "LlmProviders:{name}" config section.
/// </summary>
public class LlmProviderConfig
{
    /// <summary>Provider key (e.g. "anthropic", "openai").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL for the provider API.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key (resolved from config / env).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Default model to use when no override is given.</summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Maximum retries per call (default 3).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay in milliseconds for exponential backoff.</summary>
    public int BaseRetryDelayMs { get; set; } = 1000;

    /// <summary>Max delay cap in milliseconds.</summary>
    public int MaxRetryDelayMs { get; set; } = 30000;

    /// <summary>Circuit breaker failure threshold.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>Circuit breaker cooldown in seconds.</summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = 300;

    /// <summary>Cost per 1K prompt tokens in USD (for budget tracking).</summary>
    public decimal CostPer1KPromptTokens { get; set; }

    /// <summary>Cost per 1K completion tokens in USD.</summary>
    public decimal CostPer1KCompletionTokens { get; set; }

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Whether this provider is enabled.</summary>
    public bool Enabled { get; set; } = true;
}

// ============================================================
// LLM API Request / Response (normalized)
// ============================================================

/// <summary>
/// Normalized LLM API request payload, adapted per provider in CallLlmActivity.
/// </summary>
public class NormalizedLlmRequest
{
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
    public List<ResolvedTool>? Tools { get; set; }
}

/// <summary>
/// Normalized LLM API response, extracted from provider-specific formats.
/// </summary>
public class NormalizedLlmResponse
{
    public bool Success { get; set; }
    public string? ResponseText { get; set; }
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public List<LlmToolCall>? ToolCalls { get; set; }
}
