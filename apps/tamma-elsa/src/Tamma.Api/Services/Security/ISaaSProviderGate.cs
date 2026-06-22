namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 — the reason a gate decision resolves the way it does. Maps 1:1
/// to the design §2.4 call-LLM error envelope (consumed by 32-5's
/// <c>ManagedAgent.RunAsync</c>).
/// </summary>
public enum ProviderGateOutcome
{
    /// <summary>Pass to composition step 2 (resolve agent). HTTP 200.</summary>
    Allowed,

    /// <summary>
    /// A <c>cli-token</c> provider in SaaS, or an unknown provider
    /// (fail-closed). The endpoint maps this to HTTP 400
    /// <c>SAAS_PROVIDER_NOT_ALLOWED</c>.
    /// </summary>
    SaasProviderNotAllowed,

    /// <summary>
    /// A SaaS auth / entitlement rejection of the tenant for the managed-LLM
    /// path. The endpoint maps this to HTTP 403 <c>TENANT_NOT_ENTITLED</c>.
    /// </summary>
    TenantNotEntitled,
}

/// <summary>
/// Story 32-4 — the input to <see cref="ISaaSProviderGate.InspectAsync"/>. The
/// gate is a pure function of <c>(mode, providerName, tenantEntitlement)</c>:
/// it touches the provider NAME and the mode only — never a key, token, or
/// secret.
/// </summary>
/// <param name="ProviderName">The resolved provider key for the request.</param>
/// <param name="Role">Optional role context (audit only).</param>
/// <param name="Action">Optional action context (audit only).</param>
/// <param name="TenantId">The tenant the decision is scoped to (SaaS).</param>
public sealed record ProviderGateContext(
    string ProviderName,
    string? Role = null,
    string? Action = null,
    Guid? TenantId = null);

/// <summary>
/// Story 32-4 — the typed gate decision the call-LLM endpoint maps to the
/// design §2.4 envelope. The gate NEVER throws to signal a denial — a denial
/// is a clean typed decision so a gated request can never leak a 500.
/// </summary>
/// <param name="Allowed"><c>true</c> ⇒ proceed to step 2; <c>false</c> ⇒ map the <see cref="Outcome"/> to the envelope.</param>
/// <param name="Outcome">The §2.4 outcome category.</param>
/// <param name="Reason">Human-readable, key-free reason; <c>null</c> when allowed.</param>
/// <param name="AuthModel">The resolved auth model; <c>null</c> when the provider is unknown.</param>
/// <param name="HttpStatusHint">200 allow / 400 not-allowed / 403 not-entitled.</param>
public sealed record ProviderGateDecision(
    bool Allowed,
    ProviderGateOutcome Outcome,
    string? Reason,
    ProviderAuthModel? AuthModel,
    int HttpStatusHint)
{
    /// <summary>An allow decision (single-user no-op, or SaaS api-key + entitled).</summary>
    public static ProviderGateDecision Allow(ProviderAuthModel? model) =>
        new(true, ProviderGateOutcome.Allowed, null, model, 200);
}

/// <summary>
/// Story 32-4 — composition step 1 of the call-LLM endpoint
/// (<c>POST /api/v1/llm/call</c>), invoked by 32-5's
/// <c>ManagedAgent.RunAsync</c> BEFORE agent resolution (step 2), credential
/// resolution (step 3), and the provider call (step 5).
///
/// <para>In single-user / self-hosted mode the gate is a hard no-op — every
/// provider (including <c>cli-token</c> harness providers) ⇒ <c>Allowed</c>,
/// with zero events and zero metric increments (harness providers are a
/// legitimate local affordance). In SaaS mode it is load-bearing:
/// <c>cli-token</c> and unknown providers ⇒ <see cref="ProviderGateOutcome.SaasProviderNotAllowed"/>
/// (400, fail-closed); un-entitled tenants ⇒
/// <see cref="ProviderGateOutcome.TenantNotEntitled"/> (403); only entitled
/// <c>api-key</c> providers pass.</para>
/// </summary>
public interface ISaaSProviderGate
{
    /// <summary>
    /// Inspect the resolved provider's auth model and the tenant's
    /// entitlement, returning a typed <see cref="ProviderGateDecision"/>. On a
    /// SaaS denial emits exactly one <c>AGENT.PROVIDER.GATED</c> event + one
    /// <c>tamma.provider.gated</c> metric increment as a swallowed side effect
    /// (an event-append failure never converts the decision into a 500). NEVER
    /// throws to signal a denial; only a contract violation (a null
    /// <paramref name="ctx"/>) may throw.
    /// </summary>
    Task<ProviderGateDecision> InspectAsync(ProviderGateContext ctx, CancellationToken ct = default);
}
