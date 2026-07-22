using Tamma.Activities.LlmCall.Credentials;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (design §2.3) — the KEY-FREE wire response returned by
/// <c>POST /api/v1/llm/call</c>. Projected from the internal
/// <see cref="AgentRunResult"/> by the endpoint's status mapper (T4) under the
/// AC7 status discipline: HTTP 200 for <see cref="Success"/>==true, HTTP 200 +
/// <see cref="Success"/>==false (with the <see cref="HttpStatusCode"/>
/// preserved) for expected execution failures so the engine's
/// <c>RetryCheck</c>/circuit-breaker keep working, 400 for
/// <c>SAAS_PROVIDER_NOT_ALLOWED</c>, 403 for entitlement denial.
///
/// <para><b>Key safety (load-bearing).</b> This record — and every nested DTO —
/// exposes the <see cref="CredentialSource"/> LABEL only (<c>"byok"</c> /
/// <c>"platform"</c>). The provider API key is NEVER a property here, in logs,
/// or in events (Story 32-3 AC5; <c>LlmCallContractTests</c> reflection guard).</para>
/// </summary>
public sealed record LlmCallResponse
{
    /// <summary>Whether the managed run produced a usable response. Required.</summary>
    public required bool Success { get; init; }

    /// <summary>Final response text. <c>null</c> on failure.</summary>
    public string? Text { get; init; }

    /// <summary>Token usage (prompt/completion/total + tool-loop accounting).
    /// Populated on success AND on failure (usage accrued before the failure).</summary>
    public UsageDto Usage { get; init; } = new();

    /// <summary>Where the provider key came from: <c>"byok"</c> | <c>"platform"</c>
    /// — NEVER the key. <c>null</c> only when the run never reached credential
    /// resolution. Pricing/billing (Epics 34/35) branch on this tag.</summary>
    public string? CredentialSource { get; init; }

    /// <summary>Provider that served (or was attempted for) this run.</summary>
    public string? ProviderUsed { get; init; }

    /// <summary>Model actually used.</summary>
    public string? ModelUsed { get; init; }

    /// <summary>Metered cost (provider cost basis + price). Markup is applied
    /// only on the platform leg (34-5); BYOK token price is 0.</summary>
    public CostDto Cost { get; init; } = new();

    /// <summary>Tool calls the LLM invoked during the run. Key-free summaries.</summary>
    public IReadOnlyList<ToolCallDto> ToolCalls { get; init; } = Array.Empty<ToolCallDto>();

    /// <summary>Stable identity of the resolved agent. <c>null</c> on the legacy
    /// path that never stamps it.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>Pinned config version of the resolved agent.</summary>
    public int AgentVersion { get; init; }

    /// <summary>The role the run served.</summary>
    public string? Role { get; init; }

    /// <summary>Workflow instance id echoed back. Required.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Total wall-clock duration of the managed run, in milliseconds.</summary>
    public long DurationMs { get; init; }

    // --- failure-only (Success == false) ----------------------------------

    /// <summary>One of <c>PROVIDER_ERROR</c> | <c>PROVIDER_CREDENTIAL_UNAVAILABLE</c>
    /// | <c>BUDGET_EXCEEDED</c> | <c>LOOP_EXHAUSTED</c>. <c>null</c> on success.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable, KEY-FREE failure reason. <c>null</c> on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Upstream HTTP status (e.g. 429/502/503/504/0), PRESERVED so the
    /// engine's <c>RetryCheck</c> + circuit breaker keep working. <c>null</c> on
    /// success or when no HTTP call was made.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Story 39-9 (AC3) — the KEY-FREE content-validation block. Present when
    /// a document validator ran (the produce dispatch supplied a <c>documentType</c>);
    /// <c>null</c> otherwise (the default for the 30+ existing dispatchers). Story 39-6
    /// consumes it (violations + per-turn history) to build its
    /// <c>ValidationExhausted</c> lineage.</summary>
    public ContentValidationDto? ContentValidation { get; init; }
}

/// <summary>
/// Story 39-9 (AC3, design §wire) — the KEY-FREE content-validation projection carried
/// on <see cref="LlmCallResponse.ContentValidation"/>. Deterministic-validator output
/// only: a valid flag, the repair-turn count, the FINAL violations, and the ordered
/// per-turn history. Never carries a key, a prompt body, or a provider error.
/// </summary>
public sealed record ContentValidationDto(
    bool Valid,
    int RepairTurns,
    IReadOnlyList<ContentViolationDto> Violations,
    IReadOnlyList<RepairTurnDto> History);

/// <summary>Story 39-9 — a single domain-phrased violation (stable code + message).</summary>
public sealed record ContentViolationDto(string Code, string Message);

/// <summary>Story 39-9 — one turn's validation verdict (turn 0 = initial produce).</summary>
public sealed record RepairTurnDto(
    int Turn,
    bool Valid,
    IReadOnlyList<ContentViolationDto> Violations);

/// <summary>
/// Story 32-5 (design §2.3) — token usage projection. Token COUNT fields only —
/// never a token STRING. Mirrors the engine's
/// <c>ToolLoopTokens</c>/<c>ToolLoopTurns</c>/<c>ToolLoopExhausted</c> workflow
/// variables so the thin shim can write them back unchanged (AC5).
/// </summary>
public sealed record UsageDto
{
    /// <summary>Prompt tokens consumed.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Completion tokens consumed.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>Total tokens consumed (prompt + completion).</summary>
    public int TotalTokens { get; init; }

    /// <summary>Cumulative tokens across all tool-loop turns (0 if the loop was
    /// not enabled).</summary>
    public int ToolLoopTokens { get; init; }

    /// <summary>Number of tool-loop iterations (0 if the loop was not enabled).</summary>
    public int ToolLoopTurns { get; init; }

    /// <summary>Whether the loop exhausted <c>maxSteps</c> without a final
    /// response.</summary>
    public bool ToolLoopExhausted { get; init; }
}

/// <summary>
/// Story 32-5 (design §2.3) — metered cost. <see cref="ProviderCostUsd"/> is the
/// raw <c>IProviderPricingService.Compute</c> basis (34-11 entity);
/// <see cref="PriceUsd"/> is the billed amount after 34-5 markup on the platform
/// leg (equal to the basis with no markup, or 0 for BYOK token price). This
/// story is a producer — invoicing (35) / analytics (36) consume it.
/// </summary>
public sealed record CostDto
{
    /// <summary>Raw provider cost basis in USD.</summary>
    public decimal ProviderCostUsd { get; init; }

    /// <summary>Billed price in USD (markup applied on platform, 0 token price on
    /// BYOK).</summary>
    public decimal PriceUsd { get; init; }

    /// <summary>ISO currency code. Defaults to <c>"USD"</c>.</summary>
    public string Currency { get; init; } = "USD";
}

/// <summary>
/// Story 32-5 (design §2.3) — a KEY-FREE summary of one tool the LLM invoked
/// during the run. Mirrors the public fields of the existing
/// <see cref="Tamma.Activities.LlmCall.Models.ToolCallInfo"/> /
/// <see cref="Tamma.Activities.LlmCall.Models.LlmToolCall"/> shapes (name + id +
/// arguments JSON) — none of which carry a secret.
/// </summary>
public sealed record ToolCallDto
{
    /// <summary>Tool / function name invoked.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Provider-assigned tool-call id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>JSON-serialized arguments the LLM passed to the tool.</summary>
    public string ArgumentsJson { get; init; } = "{}";
}

/// <summary>
/// Story 32-5 — the canonical wire labels for <see cref="CredentialSource"/>.
/// The endpoint and the meter step set <see cref="LlmCallResponse.CredentialSource"/>
/// to exactly one of these (or <c>null</c>) — NEVER a free-form string and NEVER
/// the key. Derived from the <see cref="Tamma.Activities.LlmCall.Credentials.CredentialSource"/>
/// enum so the label can never drift from the resolver's notion.
/// </summary>
public static class CredentialSourceLabel
{
    /// <summary>The tenant's own bring-your-own key.</summary>
    public const string Byok = "byok";

    /// <summary>The platform-provided key.</summary>
    public const string Platform = "platform";

    /// <summary>Project the resolver enum to its tag-safe wire label.</summary>
    public static string From(CredentialSource source) =>
        source.ToString().ToLowerInvariant();
}
