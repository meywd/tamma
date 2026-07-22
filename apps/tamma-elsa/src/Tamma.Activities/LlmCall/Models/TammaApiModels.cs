using System.Text.Json;
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

    /// <summary>Story 39-9 (D2/D10) — the document-type wire KEY gating the repair
    /// ring. Additive/optional: null/omitted ⇒ no validation (the default for the
    /// 30+ existing dispatchers).</summary>
    [JsonPropertyName("documentType")] public string? DocumentType { get; init; }

    /// <summary>Story 39-9 (D10) — the issue id, additive/optional, for the
    /// <c>LLM.*</c> event tags (AC6).</summary>
    [JsonPropertyName("issueId")] public string? IssueId { get; init; }
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

    /// <summary>Story 39-9 (AC3) — the KEY-FREE content-validation block. Mirrors
    /// <c>Tamma.Api.Services.Agents.ContentValidationDto</c>. Null when no validator
    /// ran (additive — old readers ignore it).</summary>
    [JsonPropertyName("contentValidation")] public LlmCallContentValidationDto? ContentValidation { get; init; }
}

/// <summary>Story 39-9 — the content-validation projection. Mirrors
/// <c>Tamma.Api.Services.Agents.ContentValidationDto</c>.</summary>
public sealed record LlmCallContentValidationDto
{
    [JsonPropertyName("valid")] public bool Valid { get; init; }
    [JsonPropertyName("repairTurns")] public int RepairTurns { get; init; }
    [JsonPropertyName("violations")] public IReadOnlyList<LlmCallContentViolationDto> Violations { get; init; }
        = Array.Empty<LlmCallContentViolationDto>();
    [JsonPropertyName("history")] public IReadOnlyList<LlmCallRepairTurnDto> History { get; init; }
        = Array.Empty<LlmCallRepairTurnDto>();
}

/// <summary>Story 39-9 — a single violation. Mirrors <c>ContentViolationDto</c>.</summary>
public sealed record LlmCallContentViolationDto
{
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

/// <summary>Story 39-9 — one repair-turn verdict. Mirrors <c>RepairTurnDto</c>.</summary>
public sealed record LlmCallRepairTurnDto
{
    [JsonPropertyName("turn")] public int Turn { get; init; }
    [JsonPropertyName("valid")] public bool Valid { get; init; }
    [JsonPropertyName("violations")] public IReadOnlyList<LlmCallContentViolationDto> Violations { get; init; }
        = Array.Empty<LlmCallContentViolationDto>();
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

/// <summary>
/// POST body for <c>/api/engine/events</c> — a BATCH of DCB events the Elsa
/// engine drained from its in-process <c>tamma:events</c> transient list.
/// The API persists each into the caller's tenant <c>domain_events</c>.
/// </summary>
public record AppendEventsRequest(
    [property: JsonPropertyName("events")] IReadOnlyList<EngineEventRecord> Events
);

/// <summary>
/// Story 39-11 — POST body for <c>/api/engine/documents</c>. The envelope rides
/// as a JSON string (serialized via <c>DocumentJson</c>) so the API
/// re-deserializes through the same canonical options; the tenant is asserted by
/// the <c>X-Tenant-Id</c> header. camelCase to match the API DTO binding.
/// </summary>
public record PersistDocumentRequest(
    [property: JsonPropertyName("envelopeJson")] string EnvelopeJson,
    [property: JsonPropertyName("correlatingEventId")] Guid? CorrelatingEventId
);

/// <summary>
/// Story 39-11 — POST body for <c>/api/engine/documents/{documentId}/status</c>.
/// </summary>
public record SetDocumentStatusRequest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("correlatingEventId")] Guid? CorrelatingEventId
);

/// <summary>
/// POST body for <c>/api/engine/platform-events</c> — a BATCH of platform
/// events the Elsa engine forwards to the Tamma API for durable audit storage.
/// </summary>
public record AppendPlatformEventsRequest(
    [property: JsonPropertyName("events")] IReadOnlyList<PlatformEventRecord> Events
);

/// <summary>
/// Wire projection of one platform event (see
/// <c>Tamma.Api.Dtos.Engine.PlatformEventRecord</c>). camelCase to match the
/// API DTO serialization (JsonNamingPolicy.CamelCase in Program.cs).
/// TenantId/UserId travel per-event in the body; no <c>X-Tenant-Id</c> header
/// is needed because <c>EngineServiceOnly</c> auth is satisfied by the service
/// Bearer token.
/// </summary>
public record PlatformEventRecord(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("userId")] Guid? UserId,
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string?>? Tags,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("createdAt")] DateTime? CreatedAt
);

// ============================================================
// Story 38-1 (Epic 38): git-mediation wire models
//
// These mirror the JSON shapes of Tamma.Api's Services/Git request records +
// GitMediationResult for the engine→API git-mediation endpoints
// POST/PUT/GET/PATCH /api/v1/git/{owner}/{repo}/... . They live in
// Tamma.Activities (the reference graph runs Tamma.Api → Tamma.Activities, so the
// engine client cannot see Tamma.Api's types) and carry [JsonPropertyName]
// camelCase to match the API's CamelCase serialization. NONE carry a token — the
// API resolves the per-tenant credential server-side; only credentialSource (the
// LABEL) ever comes back.
// ============================================================

public sealed record GitCreateBranchRequest
{
    [JsonPropertyName("branchName")] public string BranchName { get; init; } = string.Empty;
    [JsonPropertyName("baseRef")] public string BaseRef { get; init; } = "main";
    [JsonPropertyName("conflictStrategy")] public string? ConflictStrategy { get; init; }
    [JsonPropertyName("issueNumber")] public int IssueNumber { get; init; }
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

public sealed record GitCreatePrRequest
{
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("headRef")] public string HeadRef { get; init; } = string.Empty;
    [JsonPropertyName("baseRef")] public string BaseRef { get; init; } = "main";
    [JsonPropertyName("labels")] public IReadOnlyList<string>? Labels { get; init; }
    [JsonPropertyName("reviewers")] public IReadOnlyList<string>? Reviewers { get; init; }
    [JsonPropertyName("isDraft")] public bool IsDraft { get; init; }
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

public sealed record GitMergePrRequest
{
    [JsonPropertyName("mergeStrategy")] public string? MergeStrategy { get; init; }
    [JsonPropertyName("issueNumber")] public int IssueNumber { get; init; }
    [JsonPropertyName("branchName")] public string? BranchName { get; init; }
    [JsonPropertyName("autoDeleteBranch")] public bool AutoDeleteBranch { get; init; } = true;
    [JsonPropertyName("closeAssociatedIssue")] public bool CloseAssociatedIssue { get; init; } = true;
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

public sealed record GitUpdateIssueRequest
{
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("addLabels")] public IReadOnlyList<string>? AddLabels { get; init; }
    [JsonPropertyName("removeLabels")] public IReadOnlyList<string>? RemoveLabels { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// Epic 38 follow-up #21 — engine→API wire request for
/// <c>POST /api/v1/git/{owner}/{repo}/releases</c> (deployment-pipeline release
/// step). Mirrors <c>Tamma.Api.Services.Git.CreateReleaseRequest</c>. Carries NO
/// token — the API resolves the per-tenant credential server-side.
/// </summary>
public sealed record GitCreateReleaseRequest
{
    [JsonPropertyName("tagName")] public string TagName { get; init; } = string.Empty;
    [JsonPropertyName("targetRef")] public string? TargetRef { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("draft")] public bool Draft { get; init; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    [JsonPropertyName("issueNumber")] public int IssueNumber { get; init; }
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// Engine→API wire response for the git-mediation endpoints. Mirrors
/// <c>Tamma.Api.Services.Git.GitMediationResult</c>. KEY-FREE: only the
/// <see cref="CredentialSource"/> LABEL is ever present — never the token.
/// </summary>
public sealed record GitCallResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("credentialSource")] public string? CredentialSource { get; init; }
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }

    [JsonPropertyName("branchRef")] public string? BranchRef { get; init; }
    [JsonPropertyName("baseSha")] public string? BaseSha { get; init; }
    [JsonPropertyName("conflictResolved")] public bool? ConflictResolved { get; init; }

    [JsonPropertyName("prNumber")] public int? PrNumber { get; init; }
    [JsonPropertyName("prUrl")] public string? PrUrl { get; init; }
    [JsonPropertyName("reused")] public bool? Reused { get; init; }
    [JsonPropertyName("isDraft")] public bool? IsDraft { get; init; }

    [JsonPropertyName("merged")] public bool? Merged { get; init; }
    [JsonPropertyName("mergeSha")] public string? MergeSha { get; init; }
    [JsonPropertyName("issueClosed")] public bool? IssueClosed { get; init; }
    [JsonPropertyName("branchDeleted")] public bool? BranchDeleted { get; init; }
    [JsonPropertyName("alreadyMerged")] public bool? AlreadyMerged { get; init; }

    [JsonPropertyName("issueStatus")] public string? IssueStatus { get; init; }

    [JsonPropertyName("comments")] public IReadOnlyList<GitCommentDto>? Comments { get; init; }

    // Story 38 (Phase 1) — GitHub extra-op reads.
    [JsonPropertyName("commits")] public IReadOnlyList<GitCommitSummaryDto>? Commits { get; init; }
    [JsonPropertyName("fileChanges")] public IReadOnlyList<GitFileChangeDto>? FileChanges { get; init; }

    // Epic 38 follow-up #21 — release create.
    [JsonPropertyName("releaseId")] public long? ReleaseId { get; init; }
    [JsonPropertyName("releaseUrl")] public string? ReleaseUrl { get; init; }
    [JsonPropertyName("releaseTag")] public string? ReleaseTag { get; init; }

    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("platformStatusCode")] public int? PlatformStatusCode { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>A key-free PR review comment. Mirrors <c>Tamma.Api.Services.Git.PrCommentDto</c>.</summary>
public sealed record GitCommentDto
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("body")] public string Body { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("author")] public string Author { get; init; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

/// <summary>A key-free commit. Mirrors <c>Tamma.Api.Services.Git.GitCommitDto</c> (Story 38 Phase 1).</summary>
public sealed record GitCommitSummaryDto
{
    [JsonPropertyName("sha")] public string Sha { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; init; } = string.Empty;
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; }
    [JsonPropertyName("additions")] public int Additions { get; init; }
    [JsonPropertyName("deletions")] public int Deletions { get; init; }
    [JsonPropertyName("files")] public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
}

/// <summary>A key-free file change. Mirrors <c>Tamma.Api.Services.Git.GitFileChangeDto</c> (Story 38 Phase 1).</summary>
public sealed record GitFileChangeDto
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("changeType")] public string ChangeType { get; init; } = string.Empty;
    [JsonPropertyName("additions")] public int Additions { get; init; }
    [JsonPropertyName("deletions")] public int Deletions { get; init; }
}

// ============================================================
// Story 38-2 (Epic 38): agent-dispatch mediation wire models
//
// These mirror the JSON shapes of Tamma.Api's Services/AgentDispatch request +
// result records for the engine→API agent-dispatch endpoints
// POST/GET /api/v1/agent-dispatch/{owner}/{repo}/... . They live in
// Tamma.Activities (the reference graph runs Tamma.Api → Tamma.Activities) and
// carry [JsonPropertyName] camelCase to match the API's CamelCase serialization.
// NONE carry a token — the API mints the per-repo GitHub App INSTALLATION token
// server-side; only credentialSource (the constant LABEL "installation") comes
// back. Distinct from the LLM AgentRunResult (Story 32-5) — separate namespaces.
// ============================================================

/// <summary>Engine→API request body for <c>POST .../runs</c>. Inputs are composed
/// engine-side (pure, token-free).</summary>
public sealed record AgentDispatchRunApiRequest
{
    [JsonPropertyName("workflowFileName")] public string WorkflowFileName { get; init; } = "tamma-agent.yml";
    [JsonPropertyName("ref")] public string Ref { get; init; } = string.Empty;
    [JsonPropertyName("inputs")] public Dictionary<string, string> Inputs { get; init; } = new();
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>Engine-side collect parameters for <c>GET .../runs/{id}/results</c>
/// (sent as query params, not a body).</summary>
public sealed record CollectAgentRunApiRequest
{
    public string BranchName { get; init; } = string.Empty;
    public string Conclusion { get; init; } = string.Empty;
    public string AgentProvider { get; init; } = "claude-code";
    public int DurationSeconds { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>Response for <c>POST .../runs</c>. Mirrors
/// <c>Tamma.Api.Services.AgentDispatch.AgentDispatchRunResult</c>. KEY-FREE.</summary>
public sealed record AgentDispatchRunApiResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("credentialSource")] public string? CredentialSource { get; init; }
    [JsonPropertyName("workflowRunUrl")] public string? WorkflowRunUrl { get; init; }
    [JsonPropertyName("dispatchedAt")] public DateTime DispatchedAt { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("platformStatusCode")] public int? PlatformStatusCode { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>Response for <c>GET .../runs</c> (discover) + <c>GET .../runs/{id}</c>
/// (poll). Mirrors <c>Tamma.Api.Services.AgentDispatch.AgentRunStatusResult</c>.</summary>
public sealed record AgentRunStatusApiResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("credentialSource")] public string? CredentialSource { get; init; }
    [JsonPropertyName("found")] public bool Found { get; init; }
    [JsonPropertyName("runId")] public long? RunId { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("conclusion")] public string? Conclusion { get; init; }
    [JsonPropertyName("workflowRunUrl")] public string? WorkflowRunUrl { get; init; }
    [JsonPropertyName("headBranch")] public string? HeadBranch { get; init; }
    [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; init; }
    [JsonPropertyName("artifactsUrl")] public string? ArtifactsUrl { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("platformStatusCode")] public int? PlatformStatusCode { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>Response for <c>GET .../runs/{id}/results</c>. Mirrors
/// <c>Tamma.Api.Services.AgentDispatch.AgentRunResultsResult</c>. <c>agentSuccess</c>
/// is the AGENT's task success; <c>success</c> is the mediation success.</summary>
public sealed record AgentRunResultsApiResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("credentialSource")] public string? CredentialSource { get; init; }
    [JsonPropertyName("agentSuccess")] public bool AgentSuccess { get; init; }
    [JsonPropertyName("prNumber")] public int? PrNumber { get; init; }
    [JsonPropertyName("prUrl")] public string? PrUrl { get; init; }
    [JsonPropertyName("commitSha")] public string CommitSha { get; init; } = string.Empty;
    [JsonPropertyName("filesChanged")] public IReadOnlyList<string> FilesChanged { get; init; } = Array.Empty<string>();
    [JsonPropertyName("commitsCount")] public int CommitsCount { get; init; }
    [JsonPropertyName("checksPassed")] public bool? ChecksPassed { get; init; }
    [JsonPropertyName("tokensUsed")] public int TokensUsed { get; init; }
    [JsonPropertyName("durationSeconds")] public int DurationSeconds { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("agentLogSummary")] public string? AgentLogSummary { get; init; }
    [JsonPropertyName("agentProvider")] public string? AgentProvider { get; init; }
    [JsonPropertyName("agentVersion")] public string? AgentVersion { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("platformStatusCode")] public int? PlatformStatusCode { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>Response for <c>GET .../installation</c>. Mirrors
/// <c>Tamma.Api.Services.AgentDispatch.AgentInstallationResult</c>.</summary>
public sealed record AgentInstallationApiResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("installationId")] public long? InstallationId { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

// ============================================================
// Story 38 (Phase 1): CI / JIRA / email mediation wire models
//
// These mirror the JSON shapes of Tamma.Api's Services/Ci, Services/Jira, and
// Services/EmailMediation request + result records for the engine→API endpoints
// POST/GET /api/v1/ci/... , GET/PATCH /api/v1/jira/tickets/... , and
// POST /api/v1/notifications/email . They live in Tamma.Activities (the reference
// graph runs Tamma.Api → Tamma.Activities) and carry [JsonPropertyName] camelCase to
// match the API's CamelCase serialization. NONE carry a credential — the API resolves
// it server-side; only credentialSource (the LABEL) ever comes back (CI only).
// ============================================================

/// <summary>Engine→API request for <c>POST /api/v1/ci/{owner}/{repo}/test-runs</c>.</summary>
public sealed record CiTriggerTestsRequest
{
    [JsonPropertyName("branch")] public string Branch { get; init; } = string.Empty;
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>Response for the CI-mediation endpoints. Mirrors
/// <c>Tamma.Api.Services.Ci.CiMediationResult</c>. KEY-FREE.</summary>
public sealed record CiCallResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("credentialSource")] public string? CredentialSource { get; init; }
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("testRun")] public CiTestRunDto? TestRun { get; init; }
    [JsonPropertyName("buildStatus")] public CiBuildStatusDto? BuildStatus { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("platformStatusCode")] public int? PlatformStatusCode { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>A key-free test-run projection. Mirrors <c>Tamma.Api.Services.Ci.CiTestRunDto</c>.</summary>
public sealed record CiTestRunDto
{
    [JsonPropertyName("runId")] public string RunId { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("totalTests")] public int TotalTests { get; init; }
    [JsonPropertyName("passedTests")] public int PassedTests { get; init; }
    [JsonPropertyName("failedTests")] public int FailedTests { get; init; }
    [JsonPropertyName("skippedTests")] public int SkippedTests { get; init; }
    [JsonPropertyName("coveragePercentage")] public double? CoveragePercentage { get; init; }
}

/// <summary>A key-free build-status projection. Mirrors <c>Tamma.Api.Services.Ci.CiBuildStatusDto</c>.</summary>
public sealed record CiBuildStatusDto
{
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("buildUrl")] public string? BuildUrl { get; init; }
    [JsonPropertyName("startedAt")] public DateTime? StartedAt { get; init; }
    [JsonPropertyName("finishedAt")] public DateTime? FinishedAt { get; init; }
}

/// <summary>Engine→API request for <c>PATCH /api/v1/jira/tickets/{ticketId}</c>.</summary>
public sealed record JiraUpdateTicketRequest
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("customFields")] public Dictionary<string, object>? CustomFields { get; init; }
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>Response for the JIRA-mediation endpoints. Mirrors
/// <c>Tamma.Api.Services.Jira.JiraMediationResult</c>. KEY-FREE.</summary>
public sealed record JiraCallResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("ticket")] public JiraTicketDto? Ticket { get; init; }
    [JsonPropertyName("ticketKey")] public string? TicketKey { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>A key-free JIRA ticket projection. Mirrors <c>Tamma.Api.Services.Jira.JiraTicketDto</c>.</summary>
public sealed record JiraTicketDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("assignee")] public string? Assignee { get; init; }
    [JsonPropertyName("priority")] public string? Priority { get; init; }
    [JsonPropertyName("labels")] public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}

/// <summary>Engine→API request for <c>POST /api/v1/notifications/email</c>.</summary>
public sealed record EmailSendRequest
{
    [JsonPropertyName("to")] public string To { get; init; } = string.Empty;
    [JsonPropertyName("subject")] public string Subject { get; init; } = string.Empty;
    [JsonPropertyName("body")] public string Body { get; init; } = string.Empty;
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>Response for the email-mediation endpoint. Mirrors
/// <c>Tamma.Api.Services.EmailMediation.EmailMediationResult</c>. KEY-FREE.</summary>
public sealed record EmailCallResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("txnId")] public Guid? TxnId { get; init; }
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
}

/// <summary>
/// Wire projection of one engine <c>TammaEvent</c> (see
/// <c>Tamma.Activities.Core.TammaEvent</c>). camelCase to match the API DTO
/// (<c>Tamma.Api.Dtos.Engine.EngineEventRecord</c>).
/// </summary>
public record EngineEventRecord(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("timestamp")] DateTime? Timestamp,
    [property: JsonPropertyName("durationMs")] double? DurationMs,
    [property: JsonPropertyName("activityId")] string? ActivityId,
    [property: JsonPropertyName("activityName")] string? ActivityName,
    [property: JsonPropertyName("workflowInstanceId")] string? WorkflowInstanceId,
    [property: JsonPropertyName("issueNumber")] int? IssueNumber,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string?>? Tags
);
