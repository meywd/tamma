namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 — the managed execution contract behind
/// <c>POST /api/v1/llm/call</c>. <see cref="RunAsync"/> composes the locked
/// rule-2 sequence entirely inside <c>Tamma.Api</c> (design §2.6): gate (32-4) →
/// resolve agent + per-tenant enablement (32-2/32-18/32-16) → resolve credential
/// BYOK→platform (32-3 cabinet) → render prompt (Epic 27 / custom-agent 32-17) →
/// provider call via the extracted <c>IInlineToolLoopRunner</c> (request-scoped
/// key) → meter (34-11 cost + 34-5 markup + 32-9 usage) → return.
///
/// <para><b>Distinct from CLI providers.</b> <see cref="IManagedAgent"/> is the
/// customization layer ABOVE the LLM API (provider + model + prompt + tools +
/// budget); it is NOT an <c>ICLIAgentProvider</c>. The endpoint is
/// API-provider-only — harness/CLI providers are exempt and, in SaaS, structurally
/// unreachable behind the 32-4 gate.</para>
///
/// <para>The implementation (<c>ManagedAgent</c>) and the
/// <see cref="AgentRunResult"/> → <see cref="LlmCallResponse"/> projection are
/// Task T3/T4. T1 defines this SIGNATURE only.</para>
/// </summary>
public interface IManagedAgent
{
    /// <summary>
    /// Run the managed agent for <paramref name="request"/> and return the typed
    /// <see cref="AgentRunResult"/>. ALWAYS returns a result — success, provider
    /// error, budget-exceeded, credential-unavailable, or gate-denied — so a
    /// failure never loses the run record (AC10). Fail-closed: if the gate,
    /// credential, or budget cannot be evaluated, deny (never call the provider
    /// with an empty/wrong key).
    /// </summary>
    Task<AgentRunResult> RunAsync(ManagedAgentRequest request, CancellationToken ct = default);
}
