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

    /// <summary>
    /// The agent role driving server-side prompt/persona resolution. MUST be a
    /// canonical AgentRole wire (e.g. "developer", "senior_developer", "tester",
    /// "architect") or a RolePhaseMap alias (e.g. "implementer", "reviewer") —
    /// the call-LLM endpoint's AgentResolverService 422s on an unknown role.
    /// </summary>
    public string Role { get; set; } = "developer";

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

    /// <summary>Whether to enable the agentic tool loop. Default: false (single-turn, backward compatible).</summary>
    public bool EnableToolLoop { get; set; } = false;

    /// <summary>Configuration for the agentic tool loop (only used when EnableToolLoop = true).</summary>
    public ToolLoopConfig? ToolLoopConfig { get; set; }
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

    /// <summary>Cumulative token usage across all tool loop turns (0 if tool loop was not enabled).</summary>
    public int ToolLoopTokens { get; set; }

    /// <summary>Number of tool loop iterations (0 if tool loop was not enabled).</summary>
    public int ToolLoopTurns { get; set; }

    /// <summary>Whether the tool loop exhausted maxSteps without the LLM producing a final response.</summary>
    public bool ToolLoopExhausted { get; set; }
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

    /// <summary>
    /// Story 32-3 (AC4) — where the provider API key came from for this attempt:
    /// <c>"byok"</c> (the tenant's own key) or <c>"platform"</c> (the
    /// platform-provided key). Null when the resolver was not wired (legacy
    /// platform/config path) or the attempt never reached credential
    /// resolution. NEVER the key itself — pricing/billing/benchmarking
    /// (Epics 34/35, 32-9/32-10) branch on this tag.
    /// </summary>
    public string? CredentialSource { get; set; }

    /// <summary>
    /// Story 39-9 (AC4) — a small closed-vocabulary classifier for WHY this attempt
    /// failed (see <see cref="DiagnosticFailureCodes"/>). ADDITIVE and nullable:
    /// older serialized diagnostics (JSON without this field) deserialize to
    /// <c>null</c>, and default STJ ignores it for unaware readers — existing
    /// consumers are unaffected. The load-bearing value is
    /// <see cref="DiagnosticFailureCodes.ContentValidation"/>: a diagnostic so tagged
    /// is EXCLUDED from circuit-breaker failure recording (a content failure is the
    /// provider working fine on a wrong document — it must never open the breaker).
    /// <c>null</c> ⇒ unclassified (classify only what is certain).
    /// </summary>
    public string? FailureCode { get; set; }
}

/// <summary>
/// Story 39-9 (AC4, Design Decision D6) — the small, closed vocabulary for
/// <see cref="ProviderAttemptDiagnostic.FailureCode"/>. The ONLY value with
/// behaviour attached is <see cref="ContentValidation"/> (breaker exclusion); the
/// rest classify transport/rate-limit/budget for diagnostics.
/// </summary>
public static class DiagnosticFailureCodes
{
    /// <summary>A deterministic document-validation failure — the provider worked;
    /// the output is wrong. EXCLUDED from circuit-breaker failure recording.</summary>
    public const string ContentValidation = "content_validation";

    /// <summary>A transport / connectivity / 5xx / timeout failure (breaker's business).</summary>
    public const string Transport = "transport";

    /// <summary>A provider rate-limit (HTTP 429) failure.</summary>
    public const string RateLimit = "rate_limit";

    /// <summary>A budget-exhaustion failure.</summary>
    public const string Budget = "budget";

    /// <summary>
    /// Story 39-9 (AC5, D6) — the SHARED pure predicate both diagnostic recorders use
    /// to decide whether a diagnostic counts as a PROVIDER failure for the circuit
    /// breaker. A content-validation failure counts as NEITHER a failure NOR a success
    /// (it records nothing): it must never increment the breaker's failure count, and
    /// it must never reset a healthy provider's counters either. Everything else that
    /// did not succeed is a real provider failure.
    /// </summary>
    public static bool CountsAsProviderFailure(ProviderAttemptDiagnostic d) =>
        !d.Succeeded && d.FailureCode != ContentValidation;
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
//
// The `ResolvedPrompt` class and the `PromptResolutionLevel` enum that used to
// live here were removed alongside `ResolveLlmPromptActivity` — the abandoned
// config-driven `LlmPrompts:{provider}:{role}` hierarchy. Prompt resolution is
// the Prompt Store's job: `ResolvePromptFromRegistryActivity` renders the
// `(principal, scope, role, action)` cell over `POST /api/prompts/{role}/{action}/render`.
//
// There is deliberately NO provider dimension. `LlmCallWorkflow.BuildRetryLoop`
// retries the SAME call across the provider chain (`ForEach<provider>`), so a
// provider-keyed prompt would swap the prompt mid-retry while the output
// contract still has to hold. Provider differences belong at the transport seam
// (`HttpProviderClient`), not in the prompt.

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

    /// <summary>Normalized stop reason from the provider response.</summary>
    public StopReason StopReason { get; set; } = StopReason.EndTurn;
}

// ============================================================
// Tool Execution
// ============================================================

/// <summary>
/// Normalized stop reason across providers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StopReason
{
    /// <summary>LLM finished naturally (Anthropic: end_turn, OpenAI: stop).</summary>
    EndTurn,

    /// <summary>LLM wants to call tools (Anthropic: tool_use, OpenAI: tool_calls).</summary>
    ToolUse,

    /// <summary>Hit max_tokens limit.</summary>
    MaxTokens,

    /// <summary>Unknown or unmapped stop reason.</summary>
    Unknown
}

/// <summary>
/// Result of a single tool execution within the agentic loop.
/// </summary>
public record ToolExecutionResult(
    string ToolCallId,
    string ToolName,
    bool Success,
    string Output,
    long DurationMs
)
{
    /// <summary>Error message when Success is false (convenience alias for Output in error cases).</summary>
    public string? ErrorMessage => Success ? null : Output;
}

/// <summary>
/// Configuration for the agentic tool loop.
/// </summary>
public record ToolLoopConfig
{
    /// <summary>Maximum number of LLM round-trips before forcing termination.</summary>
    public int MaxSteps { get; init; } = 20;

    /// <summary>Allowlist of tool names the LLM may invoke. Null or empty = all tools allowed.</summary>
    public string[]? AllowedTools { get; init; }

    /// <summary>Total context window size in tokens for the model being used.</summary>
    public int ContextWindowTokens { get; init; } = 200_000;

    /// <summary>Fraction of context window at which compaction is triggered (0.0-1.0).</summary>
    public double CompactionThreshold { get; init; } = 0.8;

    /// <summary>Timeout in milliseconds for individual tool executions. Default: 60000 (60s).</summary>
    public int ToolTimeoutMs { get; init; } = 60_000;

    /// <summary>Whether to enable SSE streaming for tool loop progress events.</summary>
    public bool EnableStreaming { get; init; } = false;

    /// <summary>Whether to enable parallel tool execution when multiple tools are called in a single turn. Default: false.</summary>
    public bool EnableParallelTools { get; init; } = false;
}

/// <summary>
/// Cumulative per-turn token tracker for the tool loop.
/// </summary>
public class ToolLoopTokenTracker
{
    /// <summary>Total prompt tokens consumed across all turns.</summary>
    public int TotalPromptTokens { get; set; }

    /// <summary>Total completion tokens consumed across all turns.</summary>
    public int TotalCompletionTokens { get; set; }

    /// <summary>Total tokens (prompt + completion) across all turns.</summary>
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;

    /// <summary>Number of completed turns.</summary>
    public int TurnCount { get; set; }

    /// <summary>Record a turn's token usage.</summary>
    public void RecordTurn(int promptTokens, int completionTokens)
    {
        TotalPromptTokens += promptTokens;
        TotalCompletionTokens += completionTokens;
        TurnCount++;
    }
}

/// <summary>
/// Provider-agnostic conversation message for multi-turn tool use.
/// Serialized to Anthropic or OpenAI format at the HTTP call layer.
/// </summary>
public record ConversationMessage
{
    /// <summary>Message role: "system", "user", "assistant", or "tool".</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Text content (may be null for assistant messages that only contain tool calls).</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls requested by the assistant (only present when Role = "assistant").</summary>
    public ToolCallInfo[]? ToolCalls { get; init; }

    /// <summary>Tool call ID this message is a result for (only present when Role = "tool").</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool name this result is for (only present when Role = "tool", used for Anthropic format).</summary>
    public string? ToolName { get; init; }
}

/// <summary>
/// Information about a single tool call from the LLM response.
/// </summary>
public record ToolCallInfo(
    string Id,
    string Name,
    string ArgumentsJson
);
