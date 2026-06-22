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

// ============================================================
// Story 32-5 (T5): call-LLM wire models
//
// These records mirror the JSON shapes of Tamma.Api's
// LlmCallRequest / LlmCallResponse (Services/Agents) for the
// engine→API mediation endpoint POST /api/v1/llm/call.
//
// They live in Tamma.Activities (not Tamma.Api) because the
// reference graph runs Tamma.Api → Tamma.Activities, so the
// engine client cannot see Tamma.Api's types. This mirrors the
// established pattern above (AgentResolveResult etc.): the
// engine-side wire DTOs carry [JsonPropertyName] camelCase
// attributes that match the API's CamelCase serialization
// (Program.cs ConfigureHttpJsonOptions → JsonNamingPolicy.CamelCase).
// The Tamma.Api.Tests LlmCallContractTests guard the API side; the
// shared ToolLoopConfig type (below) is the single source of truth
// for that one nested shape.
// ============================================================

/// <summary>
/// Engine→API wire request for <c>POST /api/v1/llm/call</c>. Mirrors
/// <c>Tamma.Api.Services.Agents.LlmCallRequest</c>. Carries NO provider key —
/// the API resolves the credential server-side. The body <c>tenantId</c> is
/// retained for parity but carries no server-side authority (the endpoint uses
/// the auth-derived tenant from <c>X-Tenant-Id</c>; Finding C1).
/// </summary>
public sealed record LlmCallApiRequest
{
    [JsonPropertyName("tenantId")] public Guid? TenantId { get; init; }
    [JsonPropertyName("agentId")] public Guid? AgentId { get; init; }
    [JsonPropertyName("persona")] public string? Persona { get; init; }

    /// <summary>Finding I-1 — explicit provider override (the provider KEY:
    /// <c>anthropic</c>/<c>openai</c>/<c>openrouter</c>, NOT a persona). The shim
    /// populates this from the workflow's per-iteration provider so the API honours
    /// the <c>ForEach&lt;provider&gt;</c> chain.</summary>
    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
    [JsonPropertyName("action")] public string? Action { get; init; }
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = string.Empty;
    [JsonPropertyName("variables")] public Dictionary<string, object?> Variables { get; init; } = new();
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("tools")] public IReadOnlyList<string>? Tools { get; init; }
    [JsonPropertyName("enableToolLoop")] public bool EnableToolLoop { get; init; }
    [JsonPropertyName("toolLoopConfig")] public ToolLoopConfig? ToolLoopConfig { get; init; }
    [JsonPropertyName("params")] public LlmCallApiParams Params { get; init; } = new();
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// Inference parameters carried by <see cref="LlmCallApiRequest.Params"/>.
/// Mirrors <c>Tamma.Api.Services.Agents.LlmCallParams</c>.
/// </summary>
public sealed record LlmCallApiParams
{
    [JsonPropertyName("maxTokens")] public int MaxTokens { get; init; } = 4096;
    [JsonPropertyName("temperature")] public double Temperature { get; init; } = 0.7;
    [JsonPropertyName("budgetCapUsd")] public decimal BudgetCapUsd { get; init; }
}

/// <summary>
/// Engine→API wire response for <c>POST /api/v1/llm/call</c>. Mirrors
/// <c>Tamma.Api.Services.Agents.LlmCallResponse</c>. KEY-FREE: only the
/// <see cref="CredentialSource"/> LABEL is ever present — never the key.
/// </summary>
public sealed record LlmCallApiResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("usage")] public LlmCallUsageDto Usage { get; init; } = new();
    [JsonPropertyName("credentialSource")] public string? CredentialSource { get; init; }
    [JsonPropertyName("providerUsed")] public string? ProviderUsed { get; init; }
    [JsonPropertyName("modelUsed")] public string? ModelUsed { get; init; }
    [JsonPropertyName("cost")] public LlmCallCostDto Cost { get; init; } = new();
    [JsonPropertyName("toolCalls")] public IReadOnlyList<LlmCallToolCallDto> ToolCalls { get; init; }
        = Array.Empty<LlmCallToolCallDto>();
    [JsonPropertyName("agentId")] public Guid? AgentId { get; init; }
    [JsonPropertyName("agentVersion")] public int AgentVersion { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("httpStatusCode")] public int? HttpStatusCode { get; init; }
}

/// <summary>Token usage projection. Mirrors <c>Tamma.Api.Services.Agents.UsageDto</c>.</summary>
public sealed record LlmCallUsageDto
{
    [JsonPropertyName("promptTokens")] public int PromptTokens { get; init; }
    [JsonPropertyName("completionTokens")] public int CompletionTokens { get; init; }
    [JsonPropertyName("totalTokens")] public int TotalTokens { get; init; }
    [JsonPropertyName("toolLoopTokens")] public int ToolLoopTokens { get; init; }
    [JsonPropertyName("toolLoopTurns")] public int ToolLoopTurns { get; init; }
    [JsonPropertyName("toolLoopExhausted")] public bool ToolLoopExhausted { get; init; }
}

/// <summary>Metered cost. Mirrors <c>Tamma.Api.Services.Agents.CostDto</c>.</summary>
public sealed record LlmCallCostDto
{
    [JsonPropertyName("providerCostUsd")] public decimal ProviderCostUsd { get; init; }
    [JsonPropertyName("priceUsd")] public decimal PriceUsd { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";
}

/// <summary>A key-free tool-call summary. Mirrors <c>Tamma.Api.Services.Agents.ToolCallDto</c>.</summary>
public sealed record LlmCallToolCallDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("argumentsJson")] public string ArgumentsJson { get; init; } = "{}";
}
