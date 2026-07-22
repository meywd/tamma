using Tamma.Core.Documents;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (AC10) — the INTERNAL structured outcome of a managed run.
/// <see cref="IManagedAgent.RunAsync"/> always produces one of these (success,
/// provider error, budget-exceeded, credential-unavailable, or gate-denied) —
/// <b>failures never lose the run record</b> — and the endpoint projects it to
/// the wire <see cref="LlmCallResponse"/>. It is also the producer record that
/// 32-6 (action trail), 32-8 (outcome capture) and 32-9 (usage/cost) consume.
///
/// <para>Cost stays at the provider cost basis (<c>IProviderPricingService.Compute</c>,
/// 34-11); markup is 34-5. KEY SAFETY: this record carries the
/// <see cref="CredentialSource"/> label only — never the provider API key.</para>
/// </summary>
public sealed record AgentRunResult
{
    /// <summary>Stable identity of the resolved agent. <c>null</c> on the legacy
    /// path that never stamps it.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>Pinned config version of the resolved agent.</summary>
    public int Version { get; init; }

    /// <summary>Provider that served (or was attempted for) this run.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Model actually used.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The role the run served.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Prompt/input tokens consumed (count, never a token string).</summary>
    public int InputTokens { get; init; }

    /// <summary>Completion/output tokens consumed (count, never a token string).</summary>
    public int OutputTokens { get; init; }

    /// <summary>Provider cost basis in USD (<c>IProviderPricingService.Compute</c>).</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Billed price in USD — 34-5 markup on the platform leg, <c>0</c>
    /// on the BYOK leg (rule 7). Distinct from <see cref="CostUsd"/> (the raw
    /// basis, identical across both legs). 34-5 supplies the markup; until then
    /// platform == basis (interim passthrough).</summary>
    public decimal PriceUsd { get; init; }

    /// <summary>Cumulative tokens across all tool-loop turns (0 if the loop was
    /// not enabled).</summary>
    public int ToolLoopTokens { get; init; }

    /// <summary>Number of tool-loop iterations (0 if the loop was not enabled).</summary>
    public int ToolLoopTurns { get; init; }

    /// <summary>Whether the loop exhausted <c>maxSteps</c> without a final
    /// response.</summary>
    public bool ToolLoopExhausted { get; init; }

    /// <summary>Total wall-clock duration of the run, in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Whether the run produced a usable response.</summary>
    public bool Success { get; init; }

    /// <summary>Tool calls the LLM invoked during the run (key-free summaries).</summary>
    public IReadOnlyList<ToolCallDto> ToolCalls { get; init; } = Array.Empty<ToolCallDto>();

    /// <summary>Workflow instance id.</summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Where the provider key came from: <c>"byok"</c> | <c>"platform"</c>
    /// — NEVER the key. <c>null</c> when the run never reached credential
    /// resolution.</summary>
    public string? CredentialSource { get; init; }

    /// <summary>Final response text. <c>null</c> on failure.</summary>
    public string? ResponseText { get; init; }

    // --- failure-only (Success == false) ----------------------------------

    /// <summary>One of <c>PROVIDER_ERROR</c> | <c>PROVIDER_CREDENTIAL_UNAVAILABLE</c>
    /// | <c>BUDGET_EXCEEDED</c> | <c>LOOP_EXHAUSTED</c>. <c>null</c> on success.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable, KEY-FREE failure reason. <c>null</c> on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Upstream HTTP status, PRESERVED so the engine's <c>RetryCheck</c> +
    /// circuit breaker keep working. <c>null</c> on success or when no HTTP call
    /// was made.</summary>
    public int? HttpStatusCode { get; init; }

    // --- Story 39-9 — deterministic repair-ring outcome (additive) -----------

    /// <summary>Story 39-9 — the content-validation verdict: <c>true</c> when the
    /// produced (or repaired) document passed, <c>false</c> on an exhausted content
    /// failure, <c>null</c> when no validator applied.</summary>
    public bool? ContentValid { get; init; }

    /// <summary>Story 39-9 — the number of repair turns run (0 when the initial
    /// produce validated, repair was gated off, or no validator applied).</summary>
    public int RepairTurns { get; init; }

    /// <summary>Story 39-9 (AC3) — the ordered per-turn validation history, carried
    /// on the result for the 39-6 <c>ValidationExhausted</c> escalation lineage.
    /// <c>null</c> when no validator applied.</summary>
    public IReadOnlyList<RepairTurnRecord>? RepairHistory { get; init; }

    /// <summary>Story 39-9 (AC3) — the FINAL validator violations on a content
    /// failure (empty/<c>null</c> on success or when no validator applied).</summary>
    public IReadOnlyList<DocumentViolation>? ContentViolations { get; init; }
}
