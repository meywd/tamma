using Tamma.Activities.LlmCall.Models;

namespace Tamma.Api.Services.Agents;

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
        CancellationToken ct);

    /// <summary>
    /// Finding I-1 — the provider's default model (the same
    /// <c>LoadProviderConfig</c>/<c>GetDefaultModel</c> logic the legacy activity
    /// used, now owned by the runner). <c>ManagedAgent</c> calls this when the
    /// workflow's per-iteration provider override differs from the role-resolved
    /// provider, so the role's model is not applicable to the override provider —
    /// it must run that provider with ITS default model, never a foreign one.
    /// Returns an empty string for an unknown / non-allowlisted provider (the
    /// runner's own behaviour).
    /// </summary>
    string GetDefaultModel(string provider);
}

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
