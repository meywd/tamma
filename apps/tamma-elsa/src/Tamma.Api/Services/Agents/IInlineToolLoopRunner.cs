using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Documents;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 39-9 (D1/D2) — the deterministic repair-ring plan handed to
/// <see cref="IInlineToolLoopRunner.RunAsync"/>. The runner is registry-free: it
/// sees only the composed <see cref="Validate"/> delegate (built API-side over
/// <c>DocumentTypeRegistry.Resolve(key).Validate</c> with a fence-strip / parse
/// front — a delegate cannot ride HTTP), the gate flag, and the already-clamped
/// turn cap. This keeps the ring unit-testable with fake validators.
/// </summary>
/// <param name="DocumentTypeKey">The document-type wire key (event tag only).</param>
/// <param name="Validate">Pure validator: produced-document text → verdict. Never
/// throws for a malformed payload — it returns an invalid result carrying a
/// synthetic <c>PAYLOAD_NOT_JSON</c> violation.</param>
/// <param name="RepairEnabled">Whether repair turns run for this type
/// (<c>EnabledDocumentTypes</c> membership). When <c>false</c>, the runner
/// validates ONCE and never appends a repair turn (AC9).</param>
/// <param name="MaxRepairTurns">The already-clamped
/// <c>RepairRingOptions.EffectiveMaxRepairTurns</c> (0..2) — the runner does no
/// clamping of its own.</param>
public sealed record RepairRingPlan(
    string DocumentTypeKey,
    Func<string, DocumentValidationResult> Validate,
    bool RepairEnabled,
    int MaxRepairTurns);

/// <summary>
/// Story 39-9 (D4) — the validation verdict for a single turn in the repair ring.
/// Turn 0 is the initial produce validation; turns 1..N are repair re-validations.
/// The runner returns the ordered history; <c>ManagedAgent</c> replays it into the
/// <c>LLM.*</c> DCB events (the runner stays event-store-free).
/// </summary>
/// <param name="Turn">0 = initial produce validation; 1..N = repair turns.</param>
/// <param name="Valid">Whether the document validated on this turn.</param>
/// <param name="Violations">The domain-phrased violations (empty when valid).</param>
public sealed record RepairTurnRecord(
    int Turn,
    bool Valid,
    IReadOnlyList<DocumentViolation> Violations);

/// <summary>
/// Story 32-5 (AC4) — the extracted, reusable agentic tool-loop seam.
///
/// <para>The body of this loop is the verbatim move of
/// <c>CallLlmInlineActivity.AgenticToolLoop(...)</c>: sanitize → multi-turn
/// provider call → tool-call validation → sequential/parallel tool execution →
/// tool-output sanitization + secret redaction → context compaction → token
/// accounting. It lives here (in <c>Tamma.Activities</c>) so it is shared by
/// BOTH the engine activity (today, locally) AND <c>Tamma.Api</c>'s
/// <c>ManagedAgent</c> (T3+, server-side with a request-scoped key) — there is
/// exactly one copy of the loop, never a fork.</para>
///
/// <para>KEY SAFETY: the runner uses <see cref="LlmProviderConfig.ApiKey"/> for
/// the outbound provider header only; it never logs, returns, or persists it.</para>
/// </summary>
public interface IInlineToolLoopRunner
{
    /// <summary>
    /// Run the agentic tool loop against <paramref name="provider"/> using the
    /// supplied <paramref name="providerConfig"/> (which carries the
    /// request-scoped <c>ApiKey</c>). Returns the final response plus cumulative
    /// token totals, completed turns, and whether the loop exhausted maxSteps.
    /// </summary>
    /// <param name="repair">Story 39-9 (D1) — the deterministic repair-ring plan,
    /// or <c>null</c> when no document validation applies (behaviour then byte-identical
    /// to before the ring existed). This parameter has NO DEFAULT VALUE by design:
    /// C# expression trees reject omitted optional arguments (CS0854), so a defaulted
    /// parameter would silently break strict Moq setups — an explicit parameter makes
    /// every call site a conscious edit.</param>
    Task<InlineToolLoopResult> RunAsync(
        string provider,
        LlmProviderConfig providerConfig,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        IReadOnlyList<ResolvedTool>? tools,
        bool enableToolLoop,
        ToolLoopConfig loopConfig,
        string correlationId,
        RepairRingPlan? repair,
        CancellationToken ct);

    /// <summary>
    /// Finding I-1 — the provider's default model (the same
    /// <c>LoadProviderConfig</c>/<c>GetDefaultModel</c> logic the legacy activity
    /// used, now owned by the runner). <c>ManagedAgent</c> calls this when the
    /// workflow's per-iteration provider override differs from the role-resolved
    /// provider, so the role's model is not applicable to the override provider —
    /// it must run that provider with ITS default model, never a foreign one.
    /// Returns an empty string for an unknown / non-allowlisted provider (the
    /// runner's own behaviour). Platform-scope: Story 46-1 slots the platform
    /// provider_settings row above config; callers with tenant context should
    /// use <see cref="GetDefaultModel(string, System.Guid?)"/>.
    /// </summary>
    string GetDefaultModel(string provider);

    /// <summary>
    /// Story 46-1 (AC3) — tenant-aware default model under the full four-step
    /// precedence <b>tenant/user override → platform DB row →
    /// <c>LlmProviders:{key}:DefaultModel</c> config → descriptor default</b>.
    /// <paramref name="tenantId"/> null keeps the platform-scope behaviour of
    /// <see cref="GetDefaultModel(string)"/>. Same empty-string contract.
    /// </summary>
    string GetDefaultModel(string provider, Guid? tenantId);

    /// <summary>
    /// Story 46-1 — the same resolution as
    /// <see cref="GetDefaultModel(string, Guid?)"/> WITH provenance, for the
    /// settings endpoints' <c>source</c> field. One precedence implementation,
    /// two consumers — the endpoints never restate the chain (plan D4).
    /// </summary>
    ProviderDefaultModelResolution ResolveDefaultModelWithSource(string provider, Guid? tenantId);

    /// <summary>
    /// Skip-principal overload (bug
    /// 2026-07-27-tenant-surface-cannot-name-platform-default-under-override):
    /// <paramref name="skipPrincipal"/> <c>true</c> excludes the principal
    /// (tenant/user override) leg regardless of mode — the answer is the
    /// platform DB → config → descriptor resolution a removed override would
    /// fall back to, surfaced as <c>fallbackModel</c> on the tenant model
    /// routes. <c>false</c> behaves exactly like the two-argument overload.
    /// Same empty-string contract; still the ONE precedence implementation —
    /// callers never restate the chain.
    /// </summary>
    ProviderDefaultModelResolution ResolveDefaultModelWithSource(
        string provider, Guid? tenantId, bool skipPrincipal);
}

/// <summary>
/// Story 46-1 — a resolved default model plus its provenance, produced by the
/// ONE precedence implementation in <c>InlineToolLoopRunner.ResolveDefaultModel</c>
/// and surfaced by the provider settings endpoints as the <c>source</c> field.
/// </summary>
/// <param name="Model">The resolved model id ("" = no default anywhere — the
/// caller must always specify, the legacy contract).</param>
/// <param name="Source"><c>"tenant-override"</c> (a principal row — tenant in
/// SaaS, the sole user in single-user mode) | <c>"platform-db"</c> |
/// <c>"config"</c> (the <c>LlmProviders</c> section or the legacy
/// <c>Anthropic:Model</c> key) | <c>"descriptor"</c>.</param>
public sealed record ProviderDefaultModelResolution(string Model, string Source);

/// <summary>
/// Structured outcome of an <see cref="IInlineToolLoopRunner.RunAsync"/> run.
///
/// <para>Faithful projection of the existing
/// <c>AgenticToolLoop</c> 4-tuple <c>(Response, TotalTokens, Turns, Exhausted)</c>:
/// <see cref="InputTokens"/>/<see cref="OutputTokens"/> are the loop's cumulative
/// <c>totalPromptTokens</c>/<c>totalCompletionTokens</c> (their sum equals the old
/// <c>TotalTokens</c>, and they are already written onto
/// <see cref="Response"/>'s <c>PromptTokens</c>/<c>CompletionTokens</c>).</para>
///
/// <para><see cref="ToolCalls"/> matches the story seam shape (AC4) but is empty
/// today: the verbatim loop tracks only a tool-call <i>count</i>, not per-call
/// summaries. T3 maps this record to <c>AgentRunResult</c>; per-call summary
/// collection is a follow-on, not a behaviour change in this extraction.</para>
/// </summary>
public sealed record InlineToolLoopResult
{
    /// <summary>The final LLM response (token counts already reflect cumulative totals).</summary>
    public required NormalizedLlmResponse Response { get; init; }

    /// <summary>Cumulative prompt/input tokens across all turns.</summary>
    public int InputTokens { get; init; }

    /// <summary>Cumulative completion/output tokens across all turns.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Number of completed LLM turns.</summary>
    public int Turns { get; init; }

    /// <summary>Whether the loop exhausted maxSteps without a final response.</summary>
    public bool Exhausted { get; init; }

    /// <summary>Key-free per-tool-call summaries. Empty in the verbatim extraction
    /// (the loop tracks a count only); populated by a follow-on.</summary>
    public IReadOnlyList<ToolCallSummary> ToolCalls { get; init; } = Array.Empty<ToolCallSummary>();

    // --- Story 39-9 (D1) — deterministic repair-ring outcome (additive) --------

    /// <summary>Story 39-9 — the final content-validation verdict: <c>true</c> when
    /// the produced (or repaired) document passed its validator, <c>false</c> when it
    /// still failed after exhausting the ring, and <c>null</c> when NO validator was
    /// supplied (<c>repair == null</c>) — behaviour then byte-identical to before the
    /// ring existed.</summary>
    public bool? ContentValid { get; init; }

    /// <summary>Story 39-9 — the number of repair turns actually run (0 when the
    /// initial produce validated, when repair was gated off, or when no validator
    /// was supplied). Counted SEPARATELY from <see cref="Turns"/> (tool-loop turns).</summary>
    public int RepairTurns { get; init; }

    /// <summary>Story 39-9 — the ordered per-turn validation history (turn 0 = the
    /// initial produce validation; 1..N = repair re-validations). Empty when no
    /// validator was supplied. <c>ManagedAgent</c> replays this into the <c>LLM.*</c>
    /// DCB events.</summary>
    public IReadOnlyList<RepairTurnRecord> RepairHistory { get; init; } = Array.Empty<RepairTurnRecord>();
}

/// <summary>
/// Story 32-5 (AC4) — key-free summary of a single tool call within a loop run.
/// Placeholder shape for the documented seam; not populated by the verbatim
/// extraction (follow-on work fills it from the loop's per-call tracking).
/// </summary>
public sealed record ToolCallSummary(
    string ToolCallId,
    string ToolName,
    bool Success,
    long DurationMs);
