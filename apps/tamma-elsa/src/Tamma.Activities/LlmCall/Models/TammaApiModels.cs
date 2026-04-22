using System.Text.Json.Serialization;

namespace Tamma.Activities.LlmCall.Models;

// ============================================================
// Story 9-11: Tamma API wire models
//
// These records mirror the HTTP shapes exposed by Tamma.Api
// (AgentEndpoints, ProviderEndpoints) so that the simplified
// activities can consume them without replicating logic.
// ============================================================

/// <summary>
/// Response shape for <c>GET /api/v1/agents/{role}/resolve</c>.
/// Matches <c>Tamma.Api.Services.Agents.ResolvedAgentConfig</c>.
/// </summary>
public record AgentResolveResult(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("maxTokens")] int MaxTokens,
    [property: JsonPropertyName("tokenBudget")] int TokenBudget,
    [property: JsonPropertyName("tools")] string[]? Tools,
    [property: JsonPropertyName("systemPrompt")] string? SystemPrompt,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("maxBudgetUsd")] decimal? MaxBudgetUsd,
    [property: JsonPropertyName("permissionMode")] string? PermissionMode,
    [property: JsonPropertyName("allowedTools")] string[]? AllowedTools
);

/// <summary>
/// Request body for <c>POST /api/v1/agents/resolve-for-phase</c>.
/// </summary>
public record ResolveForPhaseRequest(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("taskType")] string? TaskType,
    [property: JsonPropertyName("taskOverrides")] object? TaskOverrides
);

/// <summary>
/// Response for <c>GET /api/providers/health/providers/{key}</c>. Matches
/// <c>ProviderHealthDto</c> semantically — the fields here are those the
/// simplified activities need.
/// </summary>
public record ProviderHealthStatus(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("failures")] int Failures,
    [property: JsonPropertyName("circuitOpen")] bool CircuitOpen,
    [property: JsonPropertyName("circuitOpenUntil")] string? CircuitOpenUntil,
    [property: JsonPropertyName("halfOpen")] bool HalfOpen
);

/// <summary>
/// Response for <c>GET /api/providers/diagnostics/budget/{accountId}</c>.
/// </summary>
public record BudgetStatus(
    [property: JsonPropertyName("spent")] decimal Spent,
    [property: JsonPropertyName("limit")] decimal Limit,
    [property: JsonPropertyName("remaining")] decimal Remaining,
    [property: JsonPropertyName("percentUsed")] decimal PercentUsed
);

/// <summary>
/// POST body for <c>/api/providers/diagnostics</c>.
/// </summary>
public record DiagnosticsIngestRequest(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("promptTokens")] int PromptTokens,
    [property: JsonPropertyName("completionTokens")] int CompletionTokens,
    [property: JsonPropertyName("totalTokens")] int TotalTokens,
    [property: JsonPropertyName("costUsd")] decimal CostUsd,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("correlationId")] string? CorrelationId
);

/// <summary>
/// Thin wrapper for <c>POST /api/providers/providers/create</c>.
/// </summary>
public record ProviderCreateRequest(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("apiKeyRef")] string? ApiKeyRef,
    [property: JsonPropertyName("config")] object? Config
);

public record ProviderSessionResult(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string? Model
);

public record TaskExecuteRequest(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("model")] string? Model
);

public record TaskExecuteResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("costUsd")] decimal CostUsd,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("error")] string? Error
);
